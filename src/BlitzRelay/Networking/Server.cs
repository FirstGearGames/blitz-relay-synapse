using BlitzRelay.Protocol;
using BlitzRelay.Rooms;
using Microsoft.Extensions.Logging;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Core.Events;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BlitzRelay.Networking;

internal sealed class Server : IDisposable
{
	// SynapseSocket moves nothing between polls, so this is the relay's forwarding granularity, not a housekeeping
	// tick. On Windows a wait is rounded up to the 15.6ms system timer tick, so asking for less than this buys nothing
	// without a high-resolution timer, and measured at 513 peers the shorter interval only bought latency, not
	// throughput: fan-out CPU was the same either way.
	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(15);

	private static readonly TimeSpan PollThreadShutdownTimeout = TimeSpan.FromSeconds(5);

	private static readonly TimeSpan HostClaimTimeout = TimeSpan.FromSeconds(10);

	private static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromSeconds(10);

	private const uint MaximumTransmissionUnit = 1200;

	private const uint MaximumPacketSize = 1400;

	// A busy host fans a tick out to every client in its room, so the transport's own defaults (500 packets/second)
	// would rate-limit legitimate traffic. These are the ceilings a flooding peer has to cross to be cut off.
	private const uint MaximumPacketsPerSecond = 2000;

	private const uint MaximumBytesPerSecond = 4 * 1024 * 1024;

	private const uint MaximumReassembledPacketSize = 64 * 1024;

	private const uint KeepAliveIntervalMilliseconds = 2000;

	private const uint TimeoutMilliseconds = 30000;

	// Datagrams only leave the kernel while Poll runs, so the receive buffer has to absorb a whole poll interval of
	// fan-in from every peer at once.
	private const int SocketReceiveBufferBytes = 4 * 1024 * 1024;

	private const int SocketSendBufferBytes = 4 * 1024 * 1024;

	private readonly SynapseManager _synapseManager;

	private readonly ILogger<Server> _logger;

	private readonly Dictionary<string, Room> _roomsByCode;

	private readonly Dictionary<SynapseConnection, PeerConnection> _sessionsByConnection;

	private readonly List<PeerConnection> _pendingDisconnects;

	// Stops the poll loop when the relay is disposed without its run token having been cancelled.
	private readonly CancellationTokenSource _stopSignal;

	private Thread? _pollThread;

	// SynapseSocket is single-threaded: this lock is what keeps the poll loop and the HTTP admin threads off each
	// other's toes, so every path that reaches the engine has to hold it.
	private readonly Lock _mutex = new();

	private readonly int _port;

	private readonly byte[] _connectionKeyBytes;

	private readonly int _maximumUnreliablePayloadLength;

	private readonly int _maximumReliablePayloadLength;

	private bool _disposed;

	public Server(int port, string connectionKey, ILogger<Server> logger)
	{
		_port = port;

		_connectionKeyBytes = Encoding.UTF8.GetBytes(connectionKey);

		_logger = logger;

		_roomsByCode = new Dictionary<string, Room>(StringComparer.OrdinalIgnoreCase);

		_sessionsByConnection = new Dictionary<SynapseConnection, PeerConnection>();

		_pendingDisconnects = [];

		_stopSignal = new CancellationTokenSource();

		_maximumUnreliablePayloadLength = RelayBackpressurePolicy.MaximumUnreliablePayloadLength(MaximumTransmissionUnit);

		_maximumReliablePayloadLength = RelayBackpressurePolicy.MaximumReliablePayloadLength(MaximumTransmissionUnit, MaximumReassembledPacketSize);

		SynapseConfig synapseConfig = new()
		{
			BindEndPoints = [new IPEndPoint(IPAddress.IPv6Any, _port)],

			MaximumPacketSize = MaximumPacketSize,

			MaximumTransmissionUnit = MaximumTransmissionUnit,

			SocketReceiveBufferBytes = SocketReceiveBufferBytes,

			SocketSendBufferBytes = SocketSendBufferBytes,

			// The relay rewrites received frames in place before forwarding them, so it needs a payload buffer of its own.
			CopyReceivedPayloads = true,

			Connection =
			{
				KeepAliveIntervalMilliseconds = KeepAliveIntervalMilliseconds,

				TimeoutMilliseconds = TimeoutMilliseconds,
			},

			Reliable =
			{
				MaximumPending = RelayBackpressurePolicy.PendingReliablePacketsBeforeDisconnect,
			},

			Segment =
			{
				ReliableEnabled = true,

				// Unreliable game traffic is dropped rather than split, so a lost segment can never strand an assembly.
				UnreliableMode = UnreliableSegmentMode.Disabled,
			},

			Security =
			{
				MaximumPacketsPerSecond = MaximumPacketsPerSecond,

				MaximumBytesPerSecond = MaximumBytesPerSecond,

				MaximumReassembledPacketSize = MaximumReassembledPacketSize,
			},
		};

		_synapseManager = new SynapseManager(synapseConfig);

		_synapseManager.ConnectionEstablished += HandleConnectionEstablished;

		_synapseManager.ConnectionClosed += HandleConnectionClosed;

		_synapseManager.ConnectionFailed += HandleConnectionFailed;

		_synapseManager.PacketReceived += HandlePacketReceived;

		_synapseManager.ViolationDetected += HandleViolationDetected;

		_synapseManager.UnhandledException += HandleUnhandledException;
	}

	public Task<int> RunAsync(CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		if (!TryStart()) return Task.FromResult(1);

		Log.RelayServerStarted(_logger, _port, MaximumTransmissionUnit, KeepAliveIntervalMilliseconds, TimeoutMilliseconds);

		TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

		// SynapseSocket only receives, retransmits, acknowledges and times peers out while Poll is running, so the poll
		// loop cannot share the thread pool with the HTTP API: a pool busy serving requests would delay the poll and
		// stall the relay itself. It gets a thread of its own instead.
		_pollThread = new Thread(() => RunPollLoop(cancellationToken, completion))
		{
			// Background, so a relay that is torn down without its token being cancelled can never hold the process
			// open. Dispose still stops and joins it, so shutdown is orderly rather than merely survivable.
			IsBackground = true,

			Name = "BlitzRelay-Poll",
		};

		_pollThread.Start();

		return completion.Task;
	}

	public bool TryCreateReservedRoom(ushort maximumClients, string displayName, bool isPublic, IReadOnlyDictionary<string, string>? metadata, out RoomSnapshot? snapshot, out ErrorCode errorCode)
	{
		lock (_mutex)
		{
			string roomCode;

			do
			{
				roomCode = RoomCode.Generate();
			}
			while (_roomsByCode.ContainsKey(roomCode));

			Room room = new()
			{
				Code = roomCode,

				Kind = RoomKind.PersistentReserved,

				DisplayName = displayName,

				IsPublic = isPublic,

				MaximumClients = maximumClients,
			};

			if (metadata is not null)
			{
				foreach ((string key, string value) in metadata)
				{
					room.Metadata[key] = value;
				}
			}

			_roomsByCode.Add(room.Code, room);

			snapshot = RoomSnapshot.FromRoom(room);

			errorCode = default(ErrorCode);

			return true;
		}
	}

	public IReadOnlyList<RoomSnapshot> GetRoomSnapshots()
	{
		lock (_mutex)
		{
			RoomSnapshot[] roomSnapshots = new RoomSnapshot[_roomsByCode.Count];

			int index = 0;

			foreach (Room room in _roomsByCode.Values)
			{
				roomSnapshots[index++] = RoomSnapshot.FromRoom(room);
			}

			return roomSnapshots;
		}
	}

	public RoomSnapshot? GetRoomSnapshot(string roomCode)
	{
		lock (_mutex)
		{
			return _roomsByCode.TryGetValue(roomCode, out Room? room) ? RoomSnapshot.FromRoom(room) : null;
		}
	}

	public bool DeleteRoom(string roomCode)
	{
		lock (_mutex)
		{
			if (!_roomsByCode.TryGetValue(roomCode, out Room? room)) return false;

			CloseRoomLocked(room, ErrorCode.RoomClosed);

			return true;
		}
	}

	public bool HasRoomHostToken(string roomCode, string roomHostToken)
	{
		lock (_mutex)
		{
			return _roomsByCode.TryGetValue(roomCode, out Room? room) && !string.IsNullOrWhiteSpace(room.HostToken) && string.Equals(room.HostToken, roomHostToken, StringComparison.OrdinalIgnoreCase);
		}
	}

	public RoomSnapshot? PatchRoom(string roomCode, string? displayName, IReadOnlyDictionary<string, string>? metadataToAdd, IReadOnlyList<string>? metadataToRemove)
	{
		lock (_mutex)
		{
			if (!_roomsByCode.TryGetValue(roomCode, out Room? room)) return null;

			if (displayName is not null) room.DisplayName = displayName;

			if (metadataToRemove is not null)
			{
				foreach (string key in metadataToRemove)
				{
					room.Metadata.Remove(key);
				}
			}

			if (metadataToAdd is not null)
			{
				foreach ((string key, string value) in metadataToAdd)
				{
					room.Metadata[key] = value;
				}
			}

			return RoomSnapshot.FromRoom(room);
		}
	}

	public bool TryKickClient(string roomCode, int virtualClientId)
	{
		lock (_mutex)
		{
			if (!_roomsByCode.TryGetValue(roomCode, out Room? room)) return false;

			if (!room.ClientsByVirtualId.TryGetValue(virtualClientId, out PeerConnection? clientSession)) return false;

			Log.ClientKickedByAdmin(_logger, virtualClientId, clientSession.Signature, roomCode);

			return TryRemoveAndDisconnectClientLocked(room, virtualClientId);
		}
	}

	public void Dispose()
	{
		if (_disposed) return;

		_disposed = true;

		// Stopped and joined before the lock is taken, both because the loop takes that same lock every poll and would
		// deadlock against a Dispose holding it, and so the engine is never disposed out from under a running poll.
		_stopSignal.Cancel();

		_pollThread?.Join(PollThreadShutdownTimeout);

		_pollThread = null;

		lock (_mutex)
		{
			UnsubscribeFromTransport();

			_synapseManager.Dispose();

			_sessionsByConnection.Clear();

			_pendingDisconnects.Clear();
		}

		_stopSignal.Dispose();
	}

	// Binds dual-stack so IPv4 and IPv6 peers share one socket, falling back to IPv4 where the host has no IPv6 stack.
	private bool TryStart()
	{
		if (TryStart(new IPEndPoint(IPAddress.IPv6Any, _port), out Exception? dualStackException)) return true;

		Log.FailedToBindDualStack(_logger, _port, dualStackException!);

		if (TryStart(new IPEndPoint(IPAddress.Any, _port), out Exception? internetworkException)) return true;

		Log.FailedToStartRelay(_logger, _port, internetworkException!);

		return false;
	}

	private bool TryStart(IPEndPoint bindEndPoint, out Exception? exception)
	{
		_synapseManager.Config.BindEndPoints.Clear();

		_synapseManager.Config.BindEndPoints.Add(bindEndPoint);

		try
		{
			_synapseManager.Start();

			exception = null;

			return true;
		}
		catch (Exception startException)
		{
			exception = startException;

			return false;
		}
	}

	// Paces itself off the poll's own start so a slow poll shortens the wait instead of accumulating drift, and waits on
	// the cancellation handle so a stop is picked up immediately without involving the thread pool.
	private void RunPollLoop(CancellationToken cancellationToken, TaskCompletionSource<int> completion)
	{
		// Either the caller's token or a disposal ends the loop.
		using CancellationTokenSource loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopSignal.Token);

		try
		{
			while (!loopCancellation.IsCancellationRequested)
			{
				long pollStartedTimestamp = Stopwatch.GetTimestamp();

				Poll();

				TimeSpan remaining = PollInterval - Stopwatch.GetElapsedTime(pollStartedTimestamp);

				if (remaining > TimeSpan.Zero) loopCancellation.Token.WaitHandle.WaitOne(remaining);
			}

			completion.TrySetResult(0);
		}
		catch (Exception exception)
		{
			completion.TrySetException(exception);
		}
		finally
		{
			Log.RelayServerStopping(_logger);

			lock (_mutex)
			{
				UnsubscribeFromTransport();

				_synapseManager.Stop();

				_sessionsByConnection.Clear();

				_pendingDisconnects.Clear();
			}
		}
	}

	// Detached before the engine is stopped or disposed so a late callback cannot walk rooms that are being torn down.
	// Unsubscribing twice is harmless, and both shutdown paths do it.
	private void UnsubscribeFromTransport()
	{
		_synapseManager.ConnectionEstablished -= HandleConnectionEstablished;

		_synapseManager.ConnectionClosed -= HandleConnectionClosed;

		_synapseManager.ConnectionFailed -= HandleConnectionFailed;

		_synapseManager.PacketReceived -= HandlePacketReceived;

		_synapseManager.ViolationDetected -= HandleViolationDetected;

		_synapseManager.UnhandledException -= HandleUnhandledException;
	}

	// Pumps the transport and everything that runs off the relay's own clock. Every callback SynapseSocket raises
	// happens inside this call, on this thread, with the lock held.
	private void Poll()
	{
		lock (_mutex)
		{
			try
			{
				_synapseManager.Poll();
			}
			catch (Exception exception)
			{
				Log.TransportUnhandledException(_logger, exception);
			}

			DateTimeOffset now = DateTimeOffset.UtcNow;

			ExpirePendingHostClaimsLocked(now);

			ExpireUnauthenticatedSessionsLocked(now);

			FlushPendingDisconnectsLocked();
		}
	}

	private void HandleConnectionEstablished(ConnectionEventArgs connectionEventArgs)
	{
		lock (_mutex)
		{
			PeerConnection session = new(connectionEventArgs.Connection)
			{
				AuthenticationDeadlineUtc = DateTimeOffset.UtcNow.Add(AuthenticationTimeout),
			};

			_sessionsByConnection[connectionEventArgs.Connection] = session;

			Log.PeerConnected(_logger, session.Signature, session.Endpoint);
		}
	}

	private void HandleConnectionClosed(ConnectionEventArgs connectionEventArgs)
	{
		CloseSession(connectionEventArgs.Connection);
	}

	// Idempotent on purpose: a peer can be reported gone by both a lifecycle violation and a ConnectionClosed event, and
	// whichever arrives first is the one that tears the session down.
	private void CloseSession(SynapseConnection? connection)
	{
		if (connection is null) return;

		lock (_mutex)
		{
			if (!_sessionsByConnection.Remove(connection, out PeerConnection? session)) return;

			session.IsConnected = false;

			Log.PeerDisconnected(_logger, session.Signature, session.Endpoint, session.Role);

			PeerRole role = session.Role;

			Room? room = session.Room;

			int virtualClientId = session.VirtualClientId;

			if (room is not null && ReferenceEquals(room.PendingHostPromotionPeer, session))
			{
				bool ackReceived = room.PendingHostPromotionAckReceived;

				session.Clear();

				if (ackReceived)
				{
					room.PendingHostPromotionPeer = null;

					room.PendingHostPromotionVirtualClientId = 0;

					room.PendingHostPromotionAckReceived = false;
				}
				else
				{
					room.ClearPendingHostClaim();

					NotifyClientsHostAvailabilityLocked(room, isAvailable: false);

					_ = TryPromoteNextClientToHostLocked(room);
				}

				return;
			}

			if (role == PeerRole.Host && room is not null)
			{
				session.Clear();

				if (room.Kind == RoomKind.EphemeralHostOwned)
				{
					_roomsByCode.Remove(room.Code);

					foreach ((int key, PeerConnection client) in room.ClientsByVirtualId)
					{
						Send(client, MessageCodec.CreateDisconnected(key), isReliable: true);

						client.Clear();

						DisconnectPeer(client);
					}

					room.ClientsByVirtualId.Clear();

					Log.RoomTornDownBecauseHostDisconnected(_logger, room.Code);

					return;
				}

				room.Host = null;

				room.HostToken = null;

				room.ClearPendingHostClaim();

				NotifyClientsHostAvailabilityLocked(room, isAvailable: false);

				_ = TryPromoteNextClientToHostLocked(room);

				return;
			}

			if (role == PeerRole.Client && room is not null)
			{
				room.ClientsByVirtualId.Remove(virtualClientId);

				session.Clear();

				if (room is { HasActiveHost: true, Host: not null })
				{
					Send(room.Host, MessageCodec.CreateDisconnected(virtualClientId), isReliable: true);
				}
				else if (room is { Kind: RoomKind.PersistentReserved, HasPendingHostClaim: false })
				{
					_ = TryPromoteNextClientToHostLocked(room);
				}

				return;
			}

			session.Clear();
		}
	}

	private void HandleConnectionFailed(ConnectionFailedEventArgs connectionFailedEventArgs)
	{
		Log.ConnectionRejected(_logger, connectionFailedEventArgs.EndPoint, connectionFailedEventArgs.Reason, connectionFailedEventArgs.Message);
	}

	// Every security decision the transport makes is reported here. For a genuine violation the relay only observes: the
	// action the engine chose for itself stands, so a flooding or malformed peer is dropped exactly as SynapseSocket
	// intends.
	private ViolationAction HandleViolationDetected(ViolationEventArgs violationEventArgs)
	{
		/* A peer that left, timed out, or exhausted its reliable retries is gone rather than misbehaving. Not every one
		 * of these paths is guaranteed to also raise ConnectionClosed, and a session the relay never closes keeps its
		 * seat in a room forever, so the teardown is driven from here too. Kick lets the engine drop its own side
		 * without blacklisting an address that is entitled to reconnect. */
		if (violationEventArgs.Reason is ViolationReason.Timeout or ViolationReason.PeerDisconnect or ViolationReason.ReliableExhausted)
		{
			Log.TransportPeerLifecycleNotice(_logger, violationEventArgs.EndPoint, violationEventArgs.Signature, violationEventArgs.Reason);

			CloseSession(violationEventArgs.Connection);

			return ViolationAction.Kick;
		}

		Log.TransportViolationDetected(_logger, violationEventArgs.EndPoint, violationEventArgs.Signature, violationEventArgs.Reason, violationEventArgs.PacketSize, violationEventArgs.Details);

		return violationEventArgs.Action;
	}

	private void HandleUnhandledException(Exception exception)
	{
		Log.TransportUnhandledException(_logger, exception);
	}

	private void HandlePacketReceived(PacketReceivedEventArgs packetReceivedEventArgs)
	{
		lock (_mutex)
		{
			if (!_sessionsByConnection.TryGetValue(packetReceivedEventArgs.Connection, out PeerConnection? session))
			{
				// ConnectionEstablished is dispatched before any packet from the same connection, so this means the
				// session was torn down and the peer is still talking.
				Log.PayloadFromUnknownSession(_logger, packetReceivedEventArgs.Connection.RemoteEndPoint);

				return;
			}

			ArraySegment<byte> payload = packetReceivedEventArgs.Payload;

			bool isReliable = packetReceivedEventArgs.IsReliable;

			Log.RelayPayloadReceived(_logger, session.Signature, payload.Count, isReliable);

			if (payload.Count == 0) return;

			if (!MessageCodec.TryReadMessageType(payload, out MessageType messageType)) return;

			if (messageType == MessageType.Authenticate)
			{
				HandleAuthenticate(session, payload);

				return;
			}

			if (!session.IsAuthenticated)
			{
				Log.MessageBeforeAuthentication(_logger, session.Signature, messageType);

				return;
			}

			switch (messageType)
			{
				case MessageType.HostRegister:
				{
					HandleHostRegister(session, payload);

					break;
				}

				case MessageType.HostClaim:
				{
					HandleHostClaim(session, payload);

					break;
				}

				case MessageType.HostPromotionAck:
				{
					HandleHostPromotionAck(session, payload);

					break;
				}

				case MessageType.ClientJoin:
				{
					HandleClientJoin(session, payload);

					break;
				}

				case MessageType.Data:
				{
					HandleData(session, payload, isReliable);

					break;
				}

				case MessageType.Kick:
				{
					HandleKick(session, payload);

					break;
				}

				default:
				{
					Log.UnknownRelayMessageType(_logger, session.Signature, payload[0]);

					break;
				}
			}
		}
	}

	// SynapseSocket authenticates a peer's identity, not its right to be here, so the connection key is presented as
	// the first relay message. An unauthenticated peer can reach nothing else and is dropped when its deadline passes.
	private void HandleAuthenticate(PeerConnection session, ArraySegment<byte> payload)
	{
		if (session.IsAuthenticated) return;

		if (!MessageCodec.TryReadAuthenticateSpan(payload, out ReadOnlySpan<byte> connectionKey) || !CryptographicOperations.FixedTimeEquals(connectionKey, _connectionKeyBytes))
		{
			Log.InvalidConnectionKey(_logger, session.Signature);

			Send(session, MessageCodec.GetErrorMessage(ErrorCode.InvalidConnectionKey), isReliable: true);

			DisconnectPeer(session);

			return;
		}

		session.IsAuthenticated = true;

		Log.PeerAuthenticated(_logger, session.Signature);
	}

	private void HandleHostRegister(PeerConnection session, ArraySegment<byte> payload)
	{
		if (!MessageCodec.TryReadHostRegister(payload, out ushort maximumClients))
		{
			Log.InvalidHostRegister(_logger, session.Signature);

			Send(session, MessageCodec.GetErrorMessage(ErrorCode.RoomNotFound), isReliable: true);

			return;
		}

		if (maximumClients < 1)
		{
			Log.InvalidHostRegisterMaximumClients(_logger, session.Signature, maximumClients);

			Send(session, MessageCodec.GetErrorMessage(ErrorCode.InvalidMaximumClients), isReliable: true);

			return;
		}

		if (session.Role != PeerRole.None)
		{
			Log.HostRegisterWhileAlreadyAssignedRole(_logger, session.Signature, session.Role);

			return;
		}

		string roomCode;

		do
		{
			roomCode = RoomCode.Generate();
		}
		while (_roomsByCode.ContainsKey(roomCode));

		Room room = new()
		{
			Code = roomCode,

			Kind = RoomKind.EphemeralHostOwned,

			Host = session,

			HostToken = GenerateRandomToken(),

			MaximumClients = maximumClients,
		};

		_roomsByCode.Add(roomCode, room);

		session.Role = PeerRole.Host;

		session.Room = room;

		Log.RoomCreated(_logger, roomCode, session.Signature, room.MaximumClients);

		Send(session, MessageCodec.CreateRoomCreated(roomCode, room.HostToken), isReliable: true);
	}

	private void HandleHostClaim(PeerConnection session, ArraySegment<byte> payload)
	{
		if (!MessageCodec.TryReadHostClaim(payload, out string roomCode, out string claimToken))
		{
			Send(session, MessageCodec.GetErrorMessage(ErrorCode.InvalidHostClaim), isReliable: true);

			return;
		}

		if (session.Role != PeerRole.None)
		{
			Log.HostRegisterWhileAlreadyAssignedRole(_logger, session.Signature, session.Role);

			return;
		}

		if (!_roomsByCode.TryGetValue(roomCode, out Room? room))
		{
			Send(session, MessageCodec.GetErrorMessage(ErrorCode.RoomNotFound), isReliable: true);

			return;
		}

		ExpirePendingHostClaimLocked(room, DateTimeOffset.UtcNow);

		if (room.Kind != RoomKind.PersistentReserved || room.HasActiveHost || !room.HasPendingHostClaim || !string.Equals(room.PendingHostClaimToken, claimToken, StringComparison.OrdinalIgnoreCase))
		{
			Send(session, MessageCodec.GetErrorMessage(ErrorCode.InvalidHostClaim), isReliable: true);

			return;
		}

		room.ClearPendingHostClaim();

		room.Host = session;

		room.HostToken = GenerateRandomToken();

		session.Role = PeerRole.Host;

		session.Room = room;

		Log.RoomCreated(_logger, room.Code, session.Signature, room.MaximumClients);

		Send(session, MessageCodec.CreateRoomCreated(room.Code, room.HostToken!), isReliable: true);

		foreach (PeerConnection client in room.ClientsByVirtualId.Values)
		{
			Send(client, MessageCodec.GetHostAvailableMessage(), isReliable: true);

			Send(session, MessageCodec.CreateConnected(client.VirtualClientId), isReliable: true);
		}
	}

	private void HandleHostPromotionAck(PeerConnection session, ArraySegment<byte> payload)
	{
		if (!MessageCodec.TryReadHostPromotionAck(payload, out string roomCode, out string claimToken)) return;

		if (session.Role != PeerRole.Client || session.Room is null) return;

		if (!_roomsByCode.TryGetValue(roomCode, out Room? room)) return;

		ExpirePendingHostClaimLocked(room, DateTimeOffset.UtcNow);

		if (room.Kind != RoomKind.PersistentReserved || !room.HasPendingHostClaim || !ReferenceEquals(room.PendingHostPromotionPeer, session) || !ReferenceEquals(session.Room, room) || !string.Equals(room.PendingHostClaimToken, claimToken, StringComparison.OrdinalIgnoreCase)) return;

		room.PendingHostPromotionAckReceived = true;

		DisconnectPeer(session);
	}

	private void HandleClientJoin(PeerConnection session, ArraySegment<byte> payload)
	{
		if (!MessageCodec.TryReadClientJoin(payload, out string roomCode))
		{
			Log.InvalidClientJoin(_logger, session.Signature);

			Send(session, MessageCodec.GetErrorMessage(ErrorCode.RoomNotFound), isReliable: true);

			return;
		}

		if (session.Role != PeerRole.None)
		{
			Log.ClientJoinWhileAlreadyAssignedRole(_logger, session.Signature, session.Role);

			return;
		}

		if (!_roomsByCode.TryGetValue(roomCode, out Room? room))
		{
			Log.JoinMissingRoom(_logger, session.Signature, roomCode);

			Send(session, MessageCodec.GetErrorMessage(ErrorCode.RoomNotFound), isReliable: true);

			return;
		}

		ExpirePendingHostClaimLocked(room, DateTimeOffset.UtcNow);

		if (room.ClientsByVirtualId.Count >= room.MaximumClients)
		{
			Log.JoinFullRoom(_logger, session.Signature, room.Code);

			Send(session, MessageCodec.GetErrorMessage(ErrorCode.RoomFull), isReliable: true);

			return;
		}

		int virtualClientId = AddClientToRoomLocked(room, session);

		Log.ClientJoinedRoom(_logger, session.Signature, room.Code, virtualClientId);

		if (room is { Kind: RoomKind.PersistentReserved, HasActiveHost: false })
		{
			if (!room.HasPendingHostClaim)
			{
				_ = TryPromoteNextClientToHostLocked(room);

				return;
			}

			Send(session, MessageCodec.GetJoinSuccessMessage(), isReliable: true);

			Send(session, MessageCodec.GetHostUnavailableMessage(), isReliable: true);

			return;
		}

		Send(session, MessageCodec.GetJoinSuccessMessage(), isReliable: true);

		if (room.Host is not null)
		{
			Send(room.Host, MessageCodec.CreateConnected(virtualClientId), isReliable: true);
		}
	}

	private void HandleData(PeerConnection session, ArraySegment<byte> payload, bool isReliable)
	{
		switch (session.Role)
		{
			case PeerRole.Host:
			{
				HandleDataFromHost(session, payload, isReliable);

				break;
			}

			case PeerRole.Client:
			{
				HandleDataFromClient(session, payload, isReliable);

				break;
			}

			default:
			{
				Log.RelayDataWithoutRoomRole(_logger, session.Signature);

				break;
			}
		}
	}

	private void HandleDataFromHost(PeerConnection hostSession, ArraySegment<byte> payload, bool isReliable)
	{
		if (payload.Count < MessageCodec.HostDataHeaderSize || payload[0] != (byte)MessageType.Data)
		{
			Log.InvalidHostDataPayload(_logger, hostSession.Signature);

			return;
		}

		MessageCodec.ReadHostData(payload, out int targetVirtualClientId, out byte gameChannel, out ReadOnlySpan<byte> gamePayload);

		if (gameChannel is not ((byte)GameChannel.Reliable or (byte)GameChannel.Unreliable) || isReliable == MessageCodec.IsUnreliableGameChannel(gameChannel)) return;

		int gamePayloadLength = gamePayload.Length;

		bool isBroadcast = targetVirtualClientId == MessageCodec.BroadcastVirtualClientId;

		PeerConnection? targetClient = null;

		PeerConnection[]? broadcastClients = null;

		int broadcastRecipientCount = 0;

		Room? room = hostSession.Room;

		if (room is null || !room.HasActiveHost || !ReferenceEquals(room.Host, hostSession))
		{
			Log.HostDataWithoutAssignedRoom(_logger, hostSession.Signature);

			return;
		}

		if (isBroadcast)
		{
			broadcastRecipientCount = room.ClientsByVirtualId.Count;

			if (broadcastRecipientCount == 1)
			{
				foreach (PeerConnection client in room.ClientsByVirtualId.Values)
				{
					targetClient = client;

					break;
				}
			}
			else if (broadcastRecipientCount > 1)
			{
				broadcastClients = ArrayPool<PeerConnection>.Shared.Rent(broadcastRecipientCount);

				room.ClientsByVirtualId.Values.CopyTo(broadcastClients, 0);
			}
		}
		else if (!room.ClientsByVirtualId.TryGetValue(targetVirtualClientId, out targetClient))
		{
			Log.HostTargetedUnknownVirtualClient(_logger, hostSession.Signature, targetVirtualClientId);

			return;
		}

		// Turning the host frame into the client frame consumes the host header, so nothing above may read the
		// payload afterwards.
		ArraySegment<byte> clientPayload = MessageCodec.RewriteHostDataAsClientData(payload, gameChannel);

		if (isBroadcast)
		{
			if (broadcastRecipientCount == 0)
			{
				Log.HostBroadcast(_logger, hostSession.Signature, gamePayloadLength, gameChannel, broadcastRecipientCount);

				return;
			}

			if (broadcastClients is null)
			{
				if (targetClient is not null) SendClientData(targetClient, clientPayload, isReliable);
			}
			else
			{
				try
				{
					SendClientData(broadcastClients.AsSpan(0, broadcastRecipientCount), clientPayload, isReliable);
				}
				finally
				{
					Array.Clear(broadcastClients, 0, broadcastRecipientCount);

					ArrayPool<PeerConnection>.Shared.Return(broadcastClients);
				}
			}

			Log.HostBroadcast(_logger, hostSession.Signature, gamePayloadLength, gameChannel, broadcastRecipientCount);

			return;
		}

		if (targetClient is not null) SendClientData(targetClient, clientPayload, isReliable);
	}

	private void HandleDataFromClient(PeerConnection clientSession, ArraySegment<byte> payload, bool isReliable)
	{
		if (payload.Count < MessageCodec.ClientDataHeaderSize || payload[0] != (byte)MessageType.Data)
		{
			Log.InvalidClientDataPayload(_logger, clientSession.Signature);

			return;
		}

		MessageCodec.ReadClientData(payload, out byte gameChannel, out ReadOnlySpan<byte> gamePayload);

		if (gameChannel is not ((byte)GameChannel.Reliable or (byte)GameChannel.Unreliable) || isReliable == MessageCodec.IsUnreliableGameChannel(gameChannel)) return;

		Room? room = clientSession.Room;

		if (room is null || !room.HasActiveHost || room.Host is not { } host)
		{
			Log.ClientDataWithoutActiveHostRoom(_logger, clientSession.Signature);

			return;
		}

		SendHostData(host, clientSession.VirtualClientId, gameChannel, gamePayload, isReliable);
	}

	private void HandleKick(PeerConnection session, ArraySegment<byte> payload)
	{
		if (session.Role != PeerRole.Host)
		{
			Log.KickWithoutHostRole(_logger, session.Signature);

			return;
		}

		if (!MessageCodec.TryReadKick(payload, out int virtualClientId))
		{
			Log.InvalidKickPayload(_logger, session.Signature);

			return;
		}

		Room? room = session.Room;

		if (room is null || !room.HasActiveHost || !ReferenceEquals(room.Host, session)) return;

		if (!room.ClientsByVirtualId.TryGetValue(virtualClientId, out PeerConnection? clientSession))
		{
			Log.KickUnknownVirtualClient(_logger, session.Signature, virtualClientId);

			return;
		}

		Log.ClientKickedByHost(_logger, session.Signature, virtualClientId, clientSession.Signature);

		_ = TryRemoveAndDisconnectClientLocked(room, virtualClientId);
	}

	private bool TryRemoveAndDisconnectClientLocked(Room room, int virtualClientId)
	{
		if (!room.ClientsByVirtualId.Remove(virtualClientId, out PeerConnection? clientSession)) return false;

		Send(clientSession, MessageCodec.CreateDisconnected(virtualClientId), isReliable: true);

		clientSession.Clear();

		DisconnectPeer(clientSession);

		return true;
	}

	private void ExpirePendingHostClaimsLocked(DateTimeOffset now)
	{
		foreach (Room room in _roomsByCode.Values)
		{
			ExpirePendingHostClaimLocked(room, now);
		}
	}

	private void ExpirePendingHostClaimLocked(Room room, DateTimeOffset now)
	{
		if (!room.HasPendingHostClaim || room.PendingHostClaimExpiresAtUtc > now) return;

		PeerConnection? pendingHostPromotionPeer = room.PendingHostPromotionPeer;

		room.ClearPendingHostClaim();

		if (pendingHostPromotionPeer is not null)
		{
			pendingHostPromotionPeer.Clear();

			DisconnectPeer(pendingHostPromotionPeer);
		}

		NotifyClientsHostAvailabilityLocked(room, isAvailable: false);

		_ = TryPromoteNextClientToHostLocked(room);
	}

	private void ExpireUnauthenticatedSessionsLocked(DateTimeOffset now)
	{
		foreach (PeerConnection session in _sessionsByConnection.Values)
		{
			if (session.IsAuthenticated || !session.IsConnected || session.AuthenticationDeadlineUtc > now) continue;

			Log.AuthenticationTimedOut(_logger, session.Signature);

			DisconnectPeer(session);
		}
	}

	private static int AddClientToRoomLocked(Room room, PeerConnection session)
	{
		int virtualClientId = room.AllocateVirtualClientId();

		room.ClientsByVirtualId.Add(virtualClientId, session);

		session.Role = PeerRole.Client;

		session.Room = room;

		session.VirtualClientId = virtualClientId;

		session.RoomJoinOrder = room.AllocateJoinOrder();

		return virtualClientId;
	}

	private bool TryPromoteNextClientToHostLocked(Room room)
	{
		if (room.Kind != RoomKind.PersistentReserved || room.HasActiveHost || room.HasPendingHostClaim) return false;

		PeerConnection? candidate = null;

		foreach (PeerConnection client in room.ClientsByVirtualId.Values)
		{
			if (candidate is null || client.RoomJoinOrder < candidate.RoomJoinOrder) candidate = client;
		}

		if (candidate is null) return false;

		room.ClientsByVirtualId.Remove(candidate.VirtualClientId);

		string claimToken = GenerateRandomToken();

		room.PendingHostClaimToken = claimToken;

		room.PendingHostClaimExpiresAtUtc = DateTimeOffset.UtcNow.Add(HostClaimTimeout);

		room.PendingHostPromotionPeer = candidate;

		room.PendingHostPromotionVirtualClientId = candidate.VirtualClientId;

		Send(candidate, MessageCodec.CreateHostPromoted(room.Code, room.MaximumClients, claimToken), isReliable: true);

		return true;
	}

	private void NotifyClientsHostAvailabilityLocked(Room room, bool isAvailable)
	{
		byte[] payload = isAvailable ? MessageCodec.GetHostAvailableMessage() : MessageCodec.GetHostUnavailableMessage();

		foreach (PeerConnection client in room.ClientsByVirtualId.Values)
		{
			Send(client, payload, isReliable: true);
		}
	}

	private void CloseRoomLocked(Room room, ErrorCode errorCode)
	{
		_roomsByCode.Remove(room.Code);

		PeerConnection? pendingHostPromotionPeer = room.PendingHostPromotionPeer;

		room.ClearPendingHostClaim();

		if (pendingHostPromotionPeer is not null)
		{
			Send(pendingHostPromotionPeer, MessageCodec.GetErrorMessage(errorCode), isReliable: true);

			pendingHostPromotionPeer.Clear();

			DisconnectPeer(pendingHostPromotionPeer);
		}

		if (room.Host is not null)
		{
			Send(room.Host, MessageCodec.GetErrorMessage(errorCode), isReliable: true);

			room.Host.Clear();

			DisconnectPeer(room.Host);

			room.Host = null;
		}

		foreach (PeerConnection client in room.ClientsByVirtualId.Values)
		{
			Send(client, MessageCodec.GetErrorMessage(errorCode), isReliable: true);

			client.Clear();

			DisconnectPeer(client);
		}

		room.ClientsByVirtualId.Clear();
	}

	private static string GenerateRandomToken()
	{
		Span<byte> bytes = stackalloc byte[16];

		RandomNumberGenerator.Fill(bytes);

		return Convert.ToHexString(bytes);
	}

	private void SendClientData(PeerConnection client, ArraySegment<byte> clientPayload, bool isReliable)
	{
		if (ShouldDropRelayPayload(client, clientPayload.Count, isReliable)) return;

		Send(client, clientPayload, isReliable);
	}

	private void SendClientData(Span<PeerConnection> clients, ArraySegment<byte> clientPayload, bool isReliable)
	{
		for (int i = 0; i < clients.Length; i++)
		{
			SendClientData(clients[i], clientPayload, isReliable);
		}
	}

	private void SendHostData(PeerConnection host, int virtualClientId, byte gameChannel, ReadOnlySpan<byte> gamePayload, bool isReliable)
	{
		int payloadLength = RelayBackpressurePolicy.HostRelayPayloadLength(gamePayload.Length);

		if (ShouldDropRelayPayload(host, payloadLength, isReliable)) return;

		// The client frame is shorter than the host frame it becomes, so this direction is the one that has to build a
		// frame of its own.
		byte[] rentedPayload = ArrayPool<byte>.Shared.Rent(payloadLength);

		try
		{
			MessageCodec.WriteHostData(rentedPayload.AsSpan(0, payloadLength), virtualClientId, gameChannel, gamePayload);

			Send(host, new ArraySegment<byte>(rentedPayload, 0, payloadLength), isReliable);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rentedPayload);
		}
	}

	private bool ShouldDropRelayPayload(PeerConnection recipient, int payloadLength, bool isReliable)
	{
		if (RelayBackpressurePolicy.ShouldDropUnreliableRelayPayload(payloadLength, _maximumUnreliablePayloadLength, isReliable))
		{
			Log.OversizedUnreliableRelayPayloadDropped(_logger, recipient.Signature, payloadLength, _maximumUnreliablePayloadLength);

			return true;
		}

		if (isReliable && payloadLength > _maximumReliablePayloadLength)
		{
			Log.RelayPayloadRejectedAsTooLarge(_logger, recipient.Signature, payloadLength, _maximumReliablePayloadLength);

			return true;
		}

		return false;
	}

	private void Send(PeerConnection session, ArraySegment<byte> payload, bool isReliable)
	{
		// An admin request can reach this after the relay has been told to stop, and a stopped engine refuses sends.
		if (!session.IsConnected || !_synapseManager.IsRunning) return;

		try
		{
			_synapseManager.Send(session.Connection, payload, isReliable);
		}
		catch (InvalidOperationException invalidOperationException) when (isReliable)
		{
			// SynapseSocket refuses a reliable send once the recipient's unacknowledged queue is full: the peer is not
			// keeping up, and holding the backlog for it only costs the rest of the room.
			Log.ReliableRelayRecipientDisconnectedDueToBackpressure(_logger, session.Signature, payload.Count, RelayBackpressurePolicy.PendingReliablePacketsBeforeDisconnect, invalidOperationException);

			DisconnectPeer(session);
		}
		catch (Exception exception)
		{
			Log.FailedToSendRelayPayload(_logger, session.Signature, isReliable, exception);
		}
	}

	// Disconnecting is deferred to the end of the poll: SynapseSocket raises ConnectionClosed inline, and that handler
	// rewrites the very room collections the callers here are walking.
	private void DisconnectPeer(PeerConnection session)
	{
		if (!session.IsConnected) return;

		session.IsConnected = false;

		_pendingDisconnects.Add(session);
	}

	private void FlushPendingDisconnectsLocked()
	{
		// Indexed rather than enumerated on purpose: closing one peer can queue another (a room teardown cascades),
		// and those have to be flushed in the same pass.
		for (int i = 0; i < _pendingDisconnects.Count; i++)
		{
			PeerConnection session = _pendingDisconnects[i];

			if (session.Connection.State == ConnectionState.Disconnected) continue;

			// The peer may have handshaked again between being queued and being flushed, in which case this session is
			// stale and the connection now belongs to the session that replaced it.
			if (_sessionsByConnection.TryGetValue(session.Connection, out PeerConnection? currentSession) && !ReferenceEquals(currentSession, session)) continue;

			try
			{
				_synapseManager.Disconnect(session.Connection);
			}
			catch (Exception exception)
			{
				Log.FailedToDisconnectPeer(_logger, session.Signature, exception);
			}
		}

		_pendingDisconnects.Clear();
	}
}
