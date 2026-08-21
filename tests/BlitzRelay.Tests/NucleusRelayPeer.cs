extern alias nucleus;

using System.Net;
using nucleus::Nucleus.Components;
using nucleus::Nucleus.Connections;
using nucleus::Nucleus.Integrations.BlitzRelay;
using nucleus::Nucleus.Integrations.Newfarm;
using nucleus::Nucleus.Managers.Core;
using nucleus::Nucleus.Managers.NetworkLoop;
using nucleus::Nucleus.Managers.Transports;
using nucleus::Nucleus.Systems;
using nucleus::Nucleus.Transports;

namespace BlitzRelay.Tests;

// One engine instance carried by the relay transport, driven by hand so a test decides exactly when a tick happens.
internal sealed class NucleusRelayPeer : IDisposable
{
	public CoreManager CoreManager { get; }

	public RelayTransport Transport { get; private set; } = null!;

	// Null on a peer created without a directory, which is every test that is not about a handover.
	public NewfarmHostMigration Migration { get; private set; } = null!;

	public string RoomCode
	{
		get => Transport.RoomCode;
	}

	private NucleusRelayPeer()
	{
		CoreManager = new CoreManager();
		CoreManager.NetworkLoopManager.UseNetworkLoopStepProvider(new ManualStepProvider());
		CoreManager.NetworkLoopManager.RegisterNetworkLoopStepCallbacks(new StepGate());
	}

	public static async Task<NucleusRelayPeer> CreateAsync(int relayPort, string connectionKey, int directoryPort = 0)
	{
		NucleusRelayPeer peer = new();

		peer.Transport = (RelayTransport)await peer.CoreManager.TransportManager.AddTransportAsync<RelayTransport>();
		peer.Transport.RelayEndPoint = new IPEndPoint(IPAddress.Loopback, relayPort);
		peer.Transport.ConnectionKey = connectionKey;

		if (directoryPort != 0)
			peer.Migration = new NewfarmHostMigration(peer.CoreManager, new RelaySessionHost(peer.Transport), new IPEndPoint(IPAddress.Loopback, directoryPort));

		return peer;
	}

	// Asks for an object this peer serves. A start is finished by the loop rather than by this call, so on a freshly promoted
	// authority the object comes back in Starting with no id yet, and the caller drives until it has one.
	public NetworkSystem SpawnObject(uint platformId)
	{
		NetworkSystem networkSystem = NetworkSystemPool.Rent<NetworkSystem, TransformComponent>(CoreManager, startSystem: false)!;

		Assert.True(CoreManager.SystemManager.EnsureStartSystem(networkSystem, platformId, isSceneObject: false), "The engine would not start an object.");

		return networkSystem;
	}

	public void DespawnObject(uint systemId)
	{
		Assert.True(CoreManager.SystemManager.TryGetSystemReference(systemId, out NetworkSystem networkSystem), $"No object with id [{systemId}] to despawn.");
		Assert.True(CoreManager.SystemManager.EnsureStopSystem(networkSystem));
	}

	public bool IsClientConnected
	{
		get => Transport.TryGetConnection(Invoker.Client, out Connection connection) && connection.LocalState == LocalConnectionState.Connected;
	}

	public bool IsServerConnected
	{
		get => Transport.TryGetConnection(Invoker.Server, out Connection connection) && connection.LocalState == LocalConnectionState.Connected;
	}

	public bool HasSystem(uint systemId)
	{
		return CoreManager.SystemManager.TryGetSystemReference(systemId, out NetworkSystem networkSystem) && networkSystem.State == NetworkSystemState.Started;
	}

	public void Dispose()
	{
		Migration?.Dispose();
	}

	// Runs a whole tick of the engine, every step in order. The variable-update steps alone carry a message, but spawning and
	// replicating an object rides the tick and state steps, so a partial tick moves messages and nothing else.
	public void Tick()
	{
		NetworkLoopManager networkLoopManager = CoreManager.NetworkLoopManager;
		StepDelta stepDelta = new(0, 0, 0);

		networkLoopManager.InvokeNetworkLoopStep(NetworkLoopSteps.EarlyVariableUpdate, stepDelta);
		networkLoopManager.InvokeNetworkLoopStep(NetworkLoopSteps.EarlyTickUpdate, stepDelta);
		networkLoopManager.InvokeNetworkLoopStep(NetworkLoopSteps.EarlyStateUpdate, stepDelta);
		networkLoopManager.InvokeNetworkLoopStep(NetworkLoopSteps.LateStateUpdate, stepDelta);
		networkLoopManager.InvokeNetworkLoopStep(NetworkLoopSteps.Reconcile, stepDelta);
		networkLoopManager.InvokeNetworkLoopStep(NetworkLoopSteps.EarlyFixedUpdate, stepDelta);
		networkLoopManager.InvokeNetworkLoopStep(NetworkLoopSteps.LateFixedUpdate, stepDelta);
		networkLoopManager.InvokeNetworkLoopStep(NetworkLoopSteps.VariableUpdate, stepDelta);
		networkLoopManager.InvokeNetworkLoopStep(NetworkLoopSteps.EarlyStateWrite, stepDelta);
		networkLoopManager.InvokeNetworkLoopStep(NetworkLoopSteps.LateStateWrite, stepDelta);
		networkLoopManager.InvokeNetworkLoopStep(NetworkLoopSteps.LateTickUpdate, stepDelta);
		networkLoopManager.InvokeNetworkLoopStep(NetworkLoopSteps.LateVariableUpdate, stepDelta);
	}

	public async Task<bool> StartHostingAsync()
	{
		return await Transport.ConnectAsync(Invoker.Server) == ConnectionStateChangeResult.Success;
	}

	public async Task<bool> JoinAsync(string roomCode)
	{
		Transport.RoomCode = roomCode;

		return await Transport.ConnectAsync(Invoker.Client) == ConnectionStateChangeResult.Success;
	}

	public async Task ShutdownAsync()
	{
		await Transport.ShutdownAsync();
	}

	// A provider that never drives itself, so nothing ticks except when a test says so.
	private sealed class ManualStepProvider : INetworkLoopStepProvider
	{
		public bool IsStarted { get; private set; }

		public void Initialize(NetworkLoopManager networkLoopManager) { }

		public void Start() => IsStarted = true;

		public void Stop() => IsStarted = false;

		public void Return() { }
	}

	// Present only so a hand-driven engine has a subscriber on its steps.
	private sealed class StepGate : INetworkLoopStepCallback
	{
		public NetworkLoopSteps GetNetworkLoopSteps() =>
			NetworkLoopSteps.EarlyVariableUpdate
			| NetworkLoopSteps.EarlyTickUpdate
			| NetworkLoopSteps.EarlyStateUpdate
			| NetworkLoopSteps.LateStateUpdate
			| NetworkLoopSteps.EarlyStateWrite
			| NetworkLoopSteps.LateStateWrite
			| NetworkLoopSteps.LateTickUpdate
			| NetworkLoopSteps.LateVariableUpdate;

		public void OnNetworkLoopStep(NetworkLoopSteps networkLoopStep, StepDelta stepDelta) { }
	}
}
