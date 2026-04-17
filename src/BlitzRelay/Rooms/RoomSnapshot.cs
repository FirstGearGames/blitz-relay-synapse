namespace BlitzRelay.Rooms;

internal sealed record RoomSnapshot
(
	string Code,
	RoomKind Kind,
	bool IsPublic,
	string DisplayName,
	int MaximumClients,
	int ConnectedClientCount,
	bool HasHost,
	bool HasPendingHostClaim,
	Dictionary<string, string> Metadata
)
{
	public static RoomSnapshot FromRoom(Room room)
	{
		return new RoomSnapshot
		(
			room.Code,
			room.Kind,
			room.IsPublic,
			room.DisplayName,
			room.MaximumClients,
			room.ClientsByVirtualId.Count,
			room.HasActiveHost,
			room.HasPendingHostClaim,
			new Dictionary<string, string>(room.Metadata)
		);
	}
}
