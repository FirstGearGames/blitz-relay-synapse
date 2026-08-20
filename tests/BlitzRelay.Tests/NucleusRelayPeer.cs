extern alias nucleus;

using System.Net;
using nucleus::Nucleus.Connections;
using nucleus::Nucleus.Integrations.BlitzRelay;
using nucleus::Nucleus.Managers.Core;
using nucleus::Nucleus.Managers.NetworkLoop;
using nucleus::Nucleus.Managers.Transports;
using nucleus::Nucleus.Transports;

namespace BlitzRelay.Tests;

// One engine instance carried by the relay transport, driven by hand so a test decides exactly when a tick happens.
internal sealed class NucleusRelayPeer
{
	public CoreManager CoreManager { get; }

	public RelayTransport Transport { get; private set; } = null!;

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

	public static async Task<NucleusRelayPeer> CreateAsync(int relayPort, string connectionKey)
	{
		NucleusRelayPeer peer = new();

		peer.Transport = (RelayTransport)await peer.CoreManager.TransportManager.AddTransportAsync<RelayTransport>();
		peer.Transport.RelayEndPoint = new IPEndPoint(IPAddress.Loopback, relayPort);
		peer.Transport.ConnectionKey = connectionKey;

		return peer;
	}

	// Runs one tick of the engine, which is where the transport is polled and received packets are handled.
	public void Tick()
	{
		CoreManager.NetworkLoopManager.InvokeNetworkLoopStep(NetworkLoopSteps.LateVariableUpdate, new StepDelta(0, 0, 0));
		CoreManager.NetworkLoopManager.InvokeNetworkLoopStep(NetworkLoopSteps.EarlyVariableUpdate, new StepDelta(0, 0, 0));
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
