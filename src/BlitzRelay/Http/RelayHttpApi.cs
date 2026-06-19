using BlitzRelay.Hosting;
using BlitzRelay.Networking;
using BlitzRelay.Protocol;
using BlitzRelay.Rooms;
using BlitzRelay.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace BlitzRelay.Http;

internal static class RelayHttpApi
{
	public static WebApplication Build(RelayHostOptions relayHostOptions, Server relayServer)
	{
		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

		builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(relayHostOptions.HttpPort));

		builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));

		builder.Services.AddCors(options =>
		{
			options.AddDefaultPolicy(configure =>
			{
				configure.AllowAnyOrigin()
						 .AllowAnyMethod()
						 .AllowAnyHeader();
			});
		});

		WebApplication app = builder.Build();

		app.UseCors();

		app.MapGet("/health", () => Results.Ok());

		app.MapPost("/rooms", (HttpContext context, CreateRoomRequest createRoomRequest) => CreateRoom(context, createRoomRequest, relayServer, relayHostOptions.HttpAdminTokenBytes));

		app.MapGet("/rooms", (HttpContext context) => GetRooms(context, relayServer, relayHostOptions.HttpAdminTokenBytes));

		app.MapGet("/rooms/{roomCode}", (HttpContext context, string roomCode) => GetRoom(context, roomCode, relayServer, relayHostOptions.HttpAdminTokenBytes));

		app.MapDelete("/rooms/{roomCode}", (HttpContext context, string roomCode) => DeleteRoom(context, roomCode, relayServer, relayHostOptions.HttpAdminTokenBytes));

		app.MapPatch("/rooms/{roomCode}", (HttpContext context, string roomCode, PatchRoomRequest patchRoomRequest) => PatchRoom(context, roomCode, patchRoomRequest, relayServer, relayHostOptions.HttpAdminTokenBytes));

		app.MapDelete("/rooms/{roomCode}/clients/{virtualClientId:int}", (HttpContext context, string roomCode, int virtualClientId) => KickClient(context, roomCode, virtualClientId, relayServer, relayHostOptions.HttpAdminTokenBytes));

		return app;
	}

	private static IResult CreateRoom(HttpContext context, CreateRoomRequest request, Server relayServer, byte[] adminTokenBytes)
	{
		if (!IsAuthorised(context, adminTokenBytes)) return Results.Unauthorized();

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

	private static IResult GetRooms(HttpContext context, Server relayServer, byte[] adminTokenBytes)
	{
		return !IsAuthorised(context, adminTokenBytes) ? Results.Unauthorized() : Results.Ok(relayServer.GetRoomSnapshots());
	}

	private static IResult GetRoom(HttpContext context, string roomCode, Server relayServer, byte[] adminTokenBytes)
	{
		if (!IsAuthorised(context, adminTokenBytes)) return Results.Unauthorized();

		RoomSnapshot? snapshot = relayServer.GetRoomSnapshot(roomCode);

		return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
	}

	private static IResult DeleteRoom(HttpContext context, string roomCode, Server relayServer, byte[] adminTokenBytes)
	{
		string? bearerToken = GetBearerToken(context);

		if (!IsAuthorised(bearerToken, adminTokenBytes) && !HasRoomEditAccess(relayServer, roomCode, bearerToken)) return Results.Unauthorized();

		return relayServer.DeleteRoom(roomCode) ? Results.NoContent() : Results.NotFound();
	}

	private static string? GetBearerToken(HttpContext httpContext)
	{
		string? authorisationHeader = httpContext.Request.Headers.Authorization;

		if (string.IsNullOrWhiteSpace(authorisationHeader)) return null;

		return authorisationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorisationHeader[7..] : null;
	}

	private static bool IsAuthorised(HttpContext httpContext, byte[] adminTokenBytes)
	{
		return IsAuthorised(GetBearerToken(httpContext), adminTokenBytes);
	}

	private static bool IsAuthorised(string? bearerToken, byte[] adminTokenBytes)
	{
		if (bearerToken is null) return false;

		int bearerTokenByteCount = Encoding.UTF8.GetByteCount(bearerToken);

		byte[] bearerTokenBytes = ArrayPool<byte>.Shared.Rent(bearerTokenByteCount);

		try
		{
			Encoding.UTF8.GetBytes(bearerToken, bearerTokenBytes);

			return CryptographicOperations.FixedTimeEquals(bearerTokenBytes, adminTokenBytes);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bearerTokenBytes);

			ArrayPool<byte>.Shared.Return(bearerTokenBytes);
		}
	}

	private static bool HasRoomEditAccess(Server relayServer, string roomCode, string? bearerToken)
	{
		return !string.IsNullOrWhiteSpace(bearerToken) && relayServer.HasRoomHostToken(roomCode, bearerToken);
	}

	private static IResult PatchRoom(HttpContext context, string roomCode, PatchRoomRequest request, Server relayServer, byte[] adminTokenBytes)
	{
		string? bearerToken = GetBearerToken(context);

		if (!IsAuthorised(bearerToken, adminTokenBytes) && !HasRoomEditAccess(relayServer, roomCode, bearerToken)) return Results.Unauthorized();

		if (request.DisplayName is not null && Encoding.UTF8.GetByteCount(request.DisplayName) > 255) return Results.BadRequest($"{nameof(PatchRoomRequest.DisplayName)} must be at most 255 bytes when UTF-8 encoded.");

		RoomSnapshot? snapshot = relayServer.PatchRoom(roomCode, request.DisplayName, request.MetadataToAdd, request.MetadataToRemove);

		return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
	}

	private static IResult KickClient(HttpContext context, string roomCode, int virtualClientId, Server relayServer, byte[] adminTokenBytes)
	{
		string? bearerToken = GetBearerToken(context);

		if (!IsAuthorised(bearerToken, adminTokenBytes) && !HasRoomEditAccess(relayServer, roomCode, bearerToken)) return Results.Unauthorized();

		return relayServer.TryKickClient(roomCode, virtualClientId) ? Results.NoContent() : Results.NotFound();
	}

	public sealed record CreateRoomRequest
	(
		ushort MaximumClients = 4096,
		string DisplayName = "",
		bool IsPublic = false,
		Dictionary<string, string>? Metadata = null
	);

	public sealed record PatchRoomRequest
	(
		string? DisplayName = null,
		Dictionary<string, string>? MetadataToAdd = null,
		List<string>? MetadataToRemove = null
	);
}
