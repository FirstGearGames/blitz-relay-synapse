using Newfarm.Client;
using Newfarm.Server;
using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace BlitzRelay.Tests;

// Host migration end to end: a real relay, a real newfarm directory, real peers on real sockets. The relay is used as
// a dumb one on purpose. Every host makes a fresh EphemeralHostOwned room and takes it down with it when it goes, so
// the room code changes at every handover and none of the relay's own promotion is involved. That is the worst case a
// relay can present, which is the point of testing against it.
public sealed class NewfarmMigrationTests(ITestOutputHelper output)
{
	private const string ConnectionKey = "newfarm-migration-key";

	// What newfarm files the credential under. It never reads the credential itself, so this is only ever compared.
	private const string AdapterTag = "blitzrelay";

	private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

	[Fact]
	public void LosingTheRelayHostMovesEverySurvivorIntoANewRoom()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		relay.WaitUntilStarted();

		using NewfarmDirectoryFixture newfarm = new();

		using NewfarmMigrationPeer host = new("host", newfarm.Port, WaitTimeout);

		using NewfarmMigrationPeer firstClient = new("first-client", newfarm.Port, WaitTimeout);

		using NewfarmMigrationPeer secondClient = new("second-client", newfarm.Port, WaitTimeout);

		using NewfarmMigrationPeer thirdClient = new("third-client", newfarm.Port, WaitTimeout);

		NewfarmMigrationPeer[] clients = [firstClient, secondClient, thirdClient];

		NewfarmMigrationPeer[] everyone = [host, firstClient, secondClient, thirdClient];

		/* The host opens a session, opens a room, and tells newfarm where the room is. */

		host.Directory.CreateSession();

		PumpUntil(() => host.CreatedIdentity is not null, WaitTimeout, "newfarm to open a session", everyone);

		NewfarmSessionIdentity identity = host.CreatedIdentity!.Value;

		host.ConnectToRelay(relay.Port, ConnectionKey);

		string firstRoomCode = host.CreateRoom(maximumClients: 8);

		host.Directory.PublishCredential(AdapterTag, Encoding.UTF8.GetBytes(firstRoomCode));

		/* Clients join that room. The identity reaches them over the game connection, which is milestone 3's job; here
		   it is handed over directly, since what is under test is what they do with it afterwards. */

		for (int i = 0; i < clients.Length; i++)
		{
			clients[i].ConnectToRelay(relay.Port, ConnectionKey);

			clients[i].JoinRoom(firstRoomCode);

			host.AcceptClient();
		}

		/* Traffic flows, so the session is real before anything is broken. */

		host.BroadcastToRoom("before-the-handover");

		for (int i = 0; i < clients.Length; i++)
			Assert.Equal("before-the-handover", clients[i].WaitForRoomData());

		/* The host process dies outright, without so much as a goodbye: its relay transport and its directory client
		   both go with it, which is why it stops being pumped from here on. The survivors have no way of reaching each
		   other and nothing to go on but the session they were told about. */

		host.DropFromRelay();

		for (int i = 0; i < clients.Length; i++)
			clients[i].Directory.AwaitSession(identity);

		/* These peers noticed before the directory did, so newfarm answers by handing back the room they just came
		   from: from where it is standing the host has not been quiet long enough to be gone. A game tries what it is
		   given, finds nothing behind it, and says so. That is what puts these peers in the queue, and it is the loop
		   a real client runs. */

		PumpUntil(() =>
		{
			for (int i = 0; i < clients.Length; i++)
			{
				if (clients[i].CredentialRoomCode == firstRoomCode) clients[i].Directory.ReportCredentialUnreachable();
			}

			return TotalElections(clients) > 0;
		}, WaitTimeout, "newfarm to elect a new host", clients);

		Assert.Equal(1, TotalElections(clients));

		/* The relay has not worked out that its host is gone yet, and will not for as long as its own timeout. Every
		   client is still sitting in a room with a dead host at the top of it. Nothing about this handover came from
		   the relay, which is the whole reason for asking somewhere else. */

		for (int i = 0; i < clients.Length; i++)
			Assert.False(clients[i].Relay!.IsClosed, $"The relay dropped [{clients[i].Name}] before newfarm had elected anyone, so this test is no longer proving newfarm got there first.");

		NewfarmMigrationPeer electedClient = ElectedPeer(clients);

		output.WriteLine($"Newfarm elected [{electedClient.Name}] to host.");

		/* The elected peer makes a brand new room, which the relay names, and publishes where it is. */

		electedClient.ConnectToRelay(relay.Port, ConnectionKey);

		string secondRoomCode = electedClient.CreateRoom(maximumClients: 8);

		Assert.NotEqual(firstRoomCode, secondRoomCode);

		electedClient.Directory.PublishCredential(AdapterTag, Encoding.UTF8.GetBytes(secondRoomCode));

		/* Every other survivor is told where the session moved to, and goes there. */

		NewfarmMigrationPeer[] survivingClients = Except(clients, electedClient);

		PumpUntil(() => AllHaveCredential(survivingClients), WaitTimeout, "the other survivors to be told the new room", clients);

		for (int i = 0; i < survivingClients.Length; i++)
		{
			Assert.Equal(secondRoomCode, survivingClients[i].CredentialRoomCode);

			Assert.Equal(AdapterTag, survivingClients[i].Credential!.Value.AdapterTag);

			survivingClients[i].ConnectToRelay(relay.Port, ConnectionKey);

			survivingClients[i].JoinRoom(secondRoomCode);

			electedClient.AcceptClient();
		}

		/* Traffic flows again, both ways, in a room nobody knew the name of when the session started. */

		electedClient.BroadcastToRoom("after-the-handover");

		for (int i = 0; i < survivingClients.Length; i++)
			Assert.Equal("after-the-handover", survivingClients[i].WaitForRoomData());

		survivingClients[0].SendToHost("survivor-to-new-host");

		Assert.Equal("survivor-to-new-host", electedClient.WaitForClientData());

		/* The old host comes back, still believing it holds the session, and cannot take it. */

		host.ConnectToRelay(relay.Port, ConnectionKey);

		string strandedRoomCode = host.CreateRoom(maximumClients: 8);

		Assert.NotEqual(secondRoomCode, strandedRoomCode);

		host.Directory.StartHosting(identity);

		host.Directory.PublishCredential(AdapterTag, Encoding.UTF8.GetBytes(strandedRoomCode));

		PumpFor(TimeSpan.FromMilliseconds(600), everyone);

		/* A peer arriving now is sent to the room the session actually lives in, not the one the old host just made. */

		using NewfarmMigrationPeer lateClient = new("late-client", newfarm.Port, WaitTimeout);

		lateClient.Directory.AwaitSession(identity);

		PumpUntil(() => lateClient.Credential is not null, WaitTimeout, "the late peer to be told where the session is", [.. everyone, lateClient]);

		Assert.Equal(secondRoomCode, lateClient.CredentialRoomCode);
		Assert.Equal(0, lateClient.ElectionCount);

		lateClient.ConnectToRelay(relay.Port, ConnectionKey);

		lateClient.JoinRoom(lateClient.CredentialRoomCode);

		electedClient.AcceptClient();

		electedClient.BroadcastToRoom("late-arrival-served");

		Assert.Equal("late-arrival-served", lateClient.WaitForRoomData());
	}

	[Fact]
	public void AHostThatGivesUpItsRoomHandsTheSessionOnWithoutWaitingToBeMissed()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		relay.WaitUntilStarted();

		// A host timeout longer than the test runs for, so a handover here can only have come from the surrender.
		using NewfarmDirectoryFixture newfarm = new(config => config.HostTimeoutMilliseconds = 60000);

		using NewfarmMigrationPeer host = new("host", newfarm.Port, WaitTimeout);

		using NewfarmMigrationPeer successor = new("successor", newfarm.Port, WaitTimeout);

		NewfarmMigrationPeer[] everyone = [host, successor];

		host.Directory.CreateSession();

		PumpUntil(() => host.CreatedIdentity is not null, WaitTimeout, "newfarm to open a session", everyone);

		NewfarmSessionIdentity identity = host.CreatedIdentity!.Value;

		host.ConnectToRelay(relay.Port, ConnectionKey);

		string firstRoomCode = host.CreateRoom(maximumClients: 4);

		host.Directory.PublishCredential(AdapterTag, Encoding.UTF8.GetBytes(firstRoomCode));

		successor.ConnectToRelay(relay.Port, ConnectionKey);

		successor.JoinRoom(firstRoomCode);

		host.AcceptClient();

		/* The player hosting leaves the match, properly, so the relay tears the room down at once. Its machine is
		   still online and its newfarm client is still perfectly able to heartbeat, so nothing about its connection
		   to the directory says the session needs a new host. Only the surrender does. */

		host.LeaveRelay();

		host.Directory.SurrenderHosting();

		successor.Relay!.WaitUntilClosed(WaitTimeout);

		successor.Directory.AwaitSession(identity);

		PumpUntil(() => successor.ElectionCount > 0, WaitTimeout, "the survivor to be elected on the surrender", everyone);

		successor.ConnectToRelay(relay.Port, ConnectionKey);

		string secondRoomCode = successor.CreateRoom(maximumClients: 4);

		Assert.NotEqual(firstRoomCode, secondRoomCode);

		successor.Directory.PublishCredential(AdapterTag, Encoding.UTF8.GetBytes(secondRoomCode));

		/* The peer that gave the session up is still in it, and is told where it went like anyone else. */

		PumpUntil(() => host.Credential is not null, WaitTimeout, "the peer that gave up hosting to be told where the session went", everyone);

		Assert.Equal(secondRoomCode, host.CredentialRoomCode);

		host.ConnectToRelay(relay.Port, ConnectionKey);

		host.JoinRoom(secondRoomCode);

		successor.AcceptClient();

		successor.BroadcastToRoom("hosted-by-the-successor");

		Assert.Equal("hosted-by-the-successor", host.WaitForRoomData());
	}

	[Fact]
	public void AHostWhoseRoomDiedButWhoseHeartbeatDidNotIsStoodDownByItsPeers()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		relay.WaitUntilStarted();

		// A host timeout longer than the test runs for. Nothing here can come from the heartbeat lapsing, because the
		// heartbeat never lapses: this host stays connected to the directory throughout and is pumped to the last line.
		using NewfarmDirectoryFixture newfarm = new(config =>
		{
			config.HostTimeoutMilliseconds = 60000;

			config.HostChallengeIntervalMilliseconds = 500;

			config.HostChallengeCooldownMilliseconds = 800;
		});

		using NewfarmMigrationPeer host = new("host", newfarm.Port, WaitTimeout);

		using NewfarmMigrationPeer strandedClient = new("stranded-client", newfarm.Port, WaitTimeout);

		NewfarmMigrationPeer[] everyone = [host, strandedClient];

		host.Directory.CreateSession();

		PumpUntil(() => host.CreatedIdentity is not null, WaitTimeout, "newfarm to open a session", everyone);

		NewfarmSessionIdentity identity = host.CreatedIdentity!.Value;

		host.ConnectToRelay(relay.Port, ConnectionKey);

		string firstRoomCode = host.CreateRoom(maximumClients: 4);

		host.Directory.PublishCredential(AdapterTag, Encoding.UTF8.GetBytes(firstRoomCode));

		strandedClient.ConnectToRelay(relay.Port, ConnectionKey);

		strandedClient.JoinRoom(firstRoomCode);

		host.AcceptClient();

		/* The game half of the host dies while the machine carries on: its room is gone and it cannot make another,
		   but its connection to the directory is untouched and it keeps saying it is hosting. */

		host.DropFromRelay();

		strandedClient.DropFromRelay();

		strandedClient.Directory.AwaitSession(identity);

		/* The client cannot get back into the room, and says so. Nothing else in the system knows: the directory hears
		   a healthy heartbeat and the relay has not timed anything out. */

		PumpUntil(() =>
		{
			strandedClient.Directory.ReportCredentialUnreachable();

			return strandedClient.ElectionCount > 0;
		}, WaitTimeout, "newfarm to stand the heartbeating host down and elect the client", everyone);

		Assert.True(host.ChallengeCount > 0, "The host was never asked to prove it was hosting.");
		Assert.Equal(1, strandedClient.ElectionCount);

		/* The client hosts, and the session carries on in a room the old host has nothing to do with. */

		strandedClient.ConnectToRelay(relay.Port, ConnectionKey);

		string secondRoomCode = strandedClient.CreateRoom(maximumClients: 4);

		Assert.NotEqual(firstRoomCode, secondRoomCode);

		strandedClient.Directory.PublishCredential(AdapterTag, Encoding.UTF8.GetBytes(secondRoomCode));

		PumpUntil(() => host.Credential is not null, WaitTimeout, "the stood-down host to be told where the session went", everyone);

		Assert.Equal(secondRoomCode, host.CredentialRoomCode);
	}

	// Polls every peer's newfarm client until the condition holds, since a newfarm client only does anything when it
	// is polled, and a peer that stops being polled looks dead to the directory.
	private static void PumpUntil(Func<bool> condition, TimeSpan timeout, string description, NewfarmMigrationPeer[] peers)
	{
		long startedTimestamp = Stopwatch.GetTimestamp();

		while (Stopwatch.GetElapsedTime(startedTimestamp) < timeout)
		{
			Pump(peers);

			if (condition()) return;

			Thread.Sleep(5);
		}

		Pump(peers);

		if (!condition()) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.##}s waiting for {description}.");
	}

	private static void PumpFor(TimeSpan duration, NewfarmMigrationPeer[] peers)
	{
		long startedTimestamp = Stopwatch.GetTimestamp();

		while (Stopwatch.GetElapsedTime(startedTimestamp) < duration)
		{
			Pump(peers);

			Thread.Sleep(5);
		}
	}

	private static void Pump(NewfarmMigrationPeer[] peers)
	{
		for (int i = 0; i < peers.Length; i++)
		{
			peers[i].Directory.Poll();
		}
	}

	private static int TotalElections(NewfarmMigrationPeer[] peers)
	{
		int electionCount = 0;

		for (int i = 0; i < peers.Length; i++)
		{
			electionCount += peers[i].ElectionCount;
		}

		return electionCount;
	}

	private static NewfarmMigrationPeer ElectedPeer(NewfarmMigrationPeer[] peers)
	{
		for (int i = 0; i < peers.Length; i++)
		{
			if (peers[i].ElectionCount > 0) return peers[i];
		}

		throw new InvalidOperationException("No peer was elected.");
	}

	private static NewfarmMigrationPeer[] Except(NewfarmMigrationPeer[] peers, NewfarmMigrationPeer excluded)
	{
		List<NewfarmMigrationPeer> remaining = [];

		for (int i = 0; i < peers.Length; i++)
		{
			if (ReferenceEquals(peers[i], excluded)) continue;

			remaining.Add(peers[i]);
		}

		return [.. remaining];
	}

	private static bool AllHaveCredential(NewfarmMigrationPeer[] peers)
	{
		for (int i = 0; i < peers.Length; i++)
		{
			if (peers[i].Credential is null) return false;
		}

		return true;
	}

	// A real newfarm directory on a real loopback port, with the timings tightened so a test does not spend half a
	// minute waiting for a host to be missed.
	private sealed class NewfarmDirectoryFixture : IDisposable
	{
		public int Port
		{
			get => _server.BoundPort;
		}

		private readonly NewfarmServer _server;

		private readonly CancellationTokenSource _cancellation;

		private readonly Thread _serverThread;

		public NewfarmDirectoryFixture(Action<NewfarmServerConfig>? configure = null)
		{
			NewfarmServerConfig config = new()
			{
				Port = 0,

				HostTimeoutMilliseconds = 1500,

				WaiterTimeoutMilliseconds = 3000,

				ElectionDeadlineMilliseconds = 30000,

				SweepIntervalMilliseconds = 50,
			};

			configure?.Invoke(config);

			_server = new NewfarmServer(config);

			_cancellation = new CancellationTokenSource();

			_serverThread = new Thread(() => _server.Run(_cancellation.Token))
			{
				IsBackground = true,

				Name = "Newfarm-Migration-Test",
			};

			_serverThread.Start();

			long startedTimestamp = Stopwatch.GetTimestamp();

			while (_server.BoundPort == 0 && Stopwatch.GetElapsedTime(startedTimestamp) < TimeSpan.FromSeconds(10))
			{
				Thread.Sleep(5);
			}

			if (_server.BoundPort == 0) throw new TimeoutException("Timed out waiting for newfarm to bind.");
		}

		public void Dispose()
		{
			_cancellation.Cancel();

			_serverThread.Join(TimeSpan.FromSeconds(5));

			_server.Dispose();

			_cancellation.Dispose();
		}
	}
}
