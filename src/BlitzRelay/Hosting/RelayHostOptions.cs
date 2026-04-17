using Microsoft.Extensions.Logging;

namespace BlitzRelay.Hosting;

public sealed class RelayHostOptions
{
	public required int UdpPort { get; init; }

	public required int HttpPort { get; init; }

	public required LogLevel LogLevel { get; init; }

	public required string ConnectionKey { get; init; }

	public required string HttpAdminToken { get; init; }
}
