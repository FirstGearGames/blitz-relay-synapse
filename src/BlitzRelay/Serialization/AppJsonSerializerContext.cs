using BlitzRelay.Hosting;
using BlitzRelay.Http;
using BlitzRelay.Rooms;
using System.Text.Json.Serialization;

namespace BlitzRelay.Serialization;

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(CorsConfiguration))]
[JsonSerializable(typeof(RelayHttpApi.CreateRoomRequest))]
[JsonSerializable(typeof(RelayHttpApi.PatchRoomRequest))]
[JsonSerializable(typeof(IReadOnlyList<RoomSnapshot>))]
[JsonSerializable(typeof(RoomSnapshot))]
internal partial class AppJsonSerializerContext : JsonSerializerContext;
