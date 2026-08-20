extern alias nucleus;

using System.Diagnostics;
using nucleus::Nucleus.Components;
using nucleus::Nucleus.Connections;
using nucleus::Nucleus.Integrations.BlitzRelay;
using nucleus::Nucleus.Managers.Client;
using nucleus::Nucleus.Systems;
using nucleus::Nucleus.Transports;
using Xunit.Abstractions;

namespace BlitzRelay.Tests;

// The whole thing at once: two engine instances, a relay that kills a room with its host, and a directory that is the only way
// the survivor can be found afterwards. Nothing is stubbed anywhere in it.
public sealed class NucleusRelayMigrationTests(ITestOutputHelper output)
{
	private const string ConnectionKey = "nucleus-migration-key";

	// Platform ids for the objects the authority spawns. Any distinct values will do; these only have to differ.
	private const uint FirstObjectPlatformId = 4001;

	private const uint SecondObjectPlatformId = 4002;

	private const uint ReplacementObjectPlatformId = 4003;

	private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

	[Fact]
	public async Task TheSurvivorTakesOverTheWorldAndTheReturningPeerSeesItAsItNowStands()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		relay.WaitUntilStarted();

		using NewfarmDirectoryFixture newfarm = new();

		/* The first peer opens a session, and the relay gives it a room. Only the session identifier is worth keeping: the room
		   is the relay's, and it will not outlive this peer. */

		NucleusRelayPeer firstPeer = await NucleusRelayPeer.CreateAsync(relay.Port, ConnectionKey, newfarm.Port);

		Assert.True(await firstPeer.Migration.StartHostingAsync(), "The first peer could not start hosting the session.");

		ulong sessionId = firstPeer.Migration.SessionId;

		Assert.NotEqual(0ul, sessionId);

		output.WriteLine($"The first peer is hosting session [{sessionId:x16}] in room [{firstPeer.RoomCode}].");

		/* Two objects in the world, which is what the handover has to carry across. */

		NetworkSystem firstObject = firstPeer.SpawnObject(FirstObjectPlatformId);
		NetworkSystem secondObject = firstPeer.SpawnObject(SecondObjectPlatformId);

		uint firstObjectId = firstObject.Id;
		uint secondObjectId = secondObject.Id;

		/* The second peer joins with the identifier alone. It is never told a room code: it asks the directory, which is the
		   only arrangement that still works after a handover. */

		NucleusRelayPeer secondPeer = await NucleusRelayPeer.CreateAsync(relay.Port, ConnectionKey, newfarm.Port);

		// Set before the link can drop, because it decides what happens at the moment it does.
		secondPeer.CoreManager.ClientManager.DisconnectResetMode = DisconnectResetMode.RetainReceivedWorld;

		Assert.True(await secondPeer.Migration.JoinAsync(sessionId), "The second peer could not join the session.");

		DriveUntil(() => secondPeer.HasSystem(firstObjectId) && secondPeer.HasSystem(secondObjectId), WaitTimeout, "the second peer to receive both objects", firstPeer, secondPeer);

		output.WriteLine($"The second peer received the world: objects [{firstObjectId}] and [{secondObjectId}].");

		string firstRoomCode = firstPeer.RoomCode;

		/* The first peer goes. Its room goes with it, so the second peer's link goes too, and nothing is left to tell it
		   anything. From here on the directory is the only thing that knows this session exists. */

		await firstPeer.ShutdownAsync();
		firstPeer.Dispose();

		DriveUntil(() => secondPeer.Migration.State == RelayMigrationState.Hosting, WaitTimeout, "the second peer to be elected and start hosting", secondPeer);

		Assert.NotEqual(firstRoomCode, secondPeer.RoomCode);

		output.WriteLine($"The second peer took the session over into room [{secondPeer.RoomCode}], which is not the room it lost.");

		/* It kept the world, so both objects are still standing, and adopting made them its own to serve. */

		Assert.True(secondPeer.HasSystem(firstObjectId), "The world the second peer kept lost the first object.");
		Assert.True(secondPeer.HasSystem(secondObjectId), "The world the second peer kept lost the second object.");

		/* Now it changes the world while it is the only peer in it: one object goes, another arrives. A returning peer has to
		   see what is there now, not what was there when it left. */

		secondPeer.DespawnObject(firstObjectId);

		// A tick between the two, because a despawn is finished by the loop rather than by the call that asked for it, and a
		// system rented before that has happened is handed back without an id.
		DriveFor(TimeSpan.FromMilliseconds(200), secondPeer);

		NetworkSystem replacementObject = secondPeer.SpawnObject(ReplacementObjectPlatformId);

		DriveUntil(() => replacementObject.State == NetworkSystemState.Started, WaitTimeout, "the replacement object to finish starting", secondPeer);

		uint replacementObjectId = replacementObject.Id;

		Assert.NotEqual(NetworkSystem.UnsetId, replacementObjectId);

		DriveFor(TimeSpan.FromMilliseconds(300), secondPeer);

		Assert.False(secondPeer.HasSystem(firstObjectId), "The despawned object is still standing on the peer that despawned it.");

		output.WriteLine($"The second peer despawned object [{firstObjectId}] and spawned object [{replacementObjectId}].");

		/* The first peer comes back, as a fresh instance would: it holds the session identifier and nothing else, and asks the
		   directory where the session is now. */

		NucleusRelayPeer returningPeer = await NucleusRelayPeer.CreateAsync(relay.Port, ConnectionKey, newfarm.Port);

		Assert.True(await returningPeer.Migration.JoinAsync(sessionId), "The returning peer could not find the session.");

		Assert.Equal(secondPeer.RoomCode, returningPeer.RoomCode);

		DriveUntil(() => returningPeer.HasSystem(secondObjectId) && returningPeer.HasSystem(replacementObjectId), WaitTimeout, "the returning peer to receive the world as it now stands", secondPeer, returningPeer);

		/* Everything lines up: the object that survived, the object that replaced the one that went, and no trace of the one
		   that was despawned while this peer was away. */

		Assert.True(returningPeer.HasSystem(secondObjectId), "The returning peer did not receive the object that survived the handover.");
		Assert.True(returningPeer.HasSystem(replacementObjectId), "The returning peer did not receive the object spawned after the handover.");
		Assert.False(returningPeer.HasSystem(firstObjectId), "The returning peer was given an object that was despawned before it arrived.");

		output.WriteLine($"The returning peer sees objects [{secondObjectId}] and [{replacementObjectId}], and not [{firstObjectId}].");

		await returningPeer.ShutdownAsync();
		returningPeer.Dispose();

		await secondPeer.ShutdownAsync();
		secondPeer.Dispose();
	}

	private static void DriveUntil(Func<bool> condition, TimeSpan timeout, string description, params NucleusRelayPeer[] peers)
	{
		long startedTimestamp = Stopwatch.GetTimestamp();

		while (Stopwatch.GetElapsedTime(startedTimestamp) < timeout)
		{
			Drive(peers);

			if (condition()) return;

			Thread.Sleep(5);
		}

		Drive(peers);

		if (!condition()) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.##}s waiting for {description}.");
	}

	private static void DriveFor(TimeSpan duration, params NucleusRelayPeer[] peers)
	{
		long startedTimestamp = Stopwatch.GetTimestamp();

		while (Stopwatch.GetElapsedTime(startedTimestamp) < duration)
		{
			Drive(peers);

			Thread.Sleep(5);
		}
	}

	private static void Drive(NucleusRelayPeer[] peers)
	{
		for (int i = 0; i < peers.Length; i++)
		{
			peers[i].Migration.Poll();
			peers[i].Tick();
		}
	}
}
