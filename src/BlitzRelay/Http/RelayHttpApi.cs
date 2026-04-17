using BlitzRelay.Hosting;
using BlitzRelay.Networking;
using BlitzRelay.Protocol;
using BlitzRelay.Rooms;
using BlitzRelay.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace BlitzRelay.Http;

internal static class RelayHttpApi
{
	public static WebApplication Build(RelayHostOptions relayHostOptions, Server relayServer)
	{
		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

		builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));

		builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(relayHostOptions.HttpPort));

		WebApplication app = builder.Build();

		app.MapGet("/health", () => Results.Ok());

		app.MapPost("/rooms", (HttpContext context, CreateRoomRequest createRoomRequest) => CreateRoom(context, createRoomRequest, relayServer, relayHostOptions.HttpAdminToken));

		app.MapGet("/rooms", (HttpContext context) => GetRooms(context, relayServer, relayHostOptions.HttpAdminToken));

		app.MapGet("/rooms/{roomCode}", (HttpContext context, string roomCode) => GetRoom(context, roomCode, relayServer, relayHostOptions.HttpAdminToken));

		app.MapDelete("/rooms/{roomCode}", (HttpContext context, string roomCode) => DeleteRoom(context, roomCode, relayServer, relayHostOptions.HttpAdminToken));

		return app;
	}

	private static IResult CreateRoom(HttpContext context, CreateRoomRequest request, Server relayServer, string adminToken)
	{
		if (!IsAuthorised(context, adminToken)) return Results.Unauthorized();

		if (request.MaximumClients <= 0) return Results.BadRequest($"{nameof(CreateRoomRequest.MaximumClients)} must be greater than 0.");

		if (Encoding.UTF8.GetByteCount(request.DisplayName) > 255) return Results.BadRequest($"{nameof(CreateRoomRequest.DisplayName)} must be at most 255 bytes when UTF-8 encoded.");

		bool created = relayServer.TryCreateReservedRoom(request.MaximumClients, request.DisplayName, request.IsPublic, request.Metadata, out RoomSnapshot? snapshot, out ErrorCode errorCode);

		if (created) return Results.Ok(snapshot);

		return errorCode switch
		{
			ErrorCode.RoomExists => Results.Conflict(errorCode.ToString()),

			_ => Results.BadRequest(errorCode.ToString()),
		};
	}

	private static IResult GetRooms(HttpContext context, Server relayServer, string adminToken)
	{
		return !IsAuthorised(context, adminToken) ? Results.Unauthorized() : Results.Ok(relayServer.GetRoomSnapshots());
	}

	private static IResult GetRoom(HttpContext context, string roomCode, Server relayServer, string adminToken)
	{
		if (!IsAuthorised(context, adminToken)) return Results.Unauthorized();

		RoomSnapshot? snapshot = relayServer.GetRoomSnapshot(roomCode);

		return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
	}

	private static IResult DeleteRoom(HttpContext context, string roomCode, Server relayServer, string adminToken)
	{
		string? bearerToken = GetBearerToken(context);

		if (!IsAuthorised(bearerToken, adminToken) && !HasRoomEditAccess(relayServer, roomCode, bearerToken)) return Results.Unauthorized();

		return relayServer.DeleteRoom(roomCode) ? Results.NoContent() : Results.NotFound();
	}

	private static string? GetBearerToken(HttpContext httpContext)
	{
		string? authorisationHeader = httpContext.Request.Headers.Authorization;

		if (string.IsNullOrWhiteSpace(authorisationHeader)) return null;

		return authorisationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorisationHeader[7..] : null;
	}

	private static bool IsAuthorised(HttpContext httpContext, string adminKey)
	{
		return IsAuthorised(GetBearerToken(httpContext), adminKey);
	}

	private static bool IsAuthorised(string? bearerToken, string adminKey)
	{
		return bearerToken == adminKey;
	}

	private static bool HasRoomEditAccess(Server relayServer, string roomCode, string? bearerToken)
	{
		return !string.IsNullOrWhiteSpace(bearerToken) && relayServer.HasRoomHostToken(roomCode, bearerToken);
	}

	public sealed record CreateRoomRequest
	(
		ushort MaximumClients = 4096,
		string DisplayName = "",
		bool IsPublic = false,
		Dictionary<string, string>? Metadata = null
	);
}
