using BlitzRelay.Http;
using BlitzRelay.Networking;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Text.Json;

namespace BlitzRelay.Hosting;

internal static class Program
{
	private const string ConnectionKeyEnvironmentVariable = "BLITZ_RELAY_CONNECTION_KEY";

	private const string HttpAdminTokenEnvironmentVariable = "BLITZ_RELAY_HTTP_ADMIN_TOKEN";

	private const string PortOptionName = "--port";

	private const string PortOptionShortName = "-p";

	private const string HttpPortOptionName = "--http-port";

	private const string LogLevelOptionName = "--log-level";

	private const string LogLevelOptionShortName = "-l";

	private const string CorsOptionName = "--cors";

	private static async Task<int> Main(string[] args)
	{
		string? connectionKey = Environment.GetEnvironmentVariable(ConnectionKeyEnvironmentVariable);

		if (string.IsNullOrWhiteSpace(connectionKey))
		{
			await Console.Error.WriteLineAsync($"'{ConnectionKeyEnvironmentVariable}' environment variable is not set.");

			return 1;
		}

		string? httpAdminToken = Environment.GetEnvironmentVariable(HttpAdminTokenEnvironmentVariable);

		if (string.IsNullOrWhiteSpace(httpAdminToken))
		{
			await Console.Error.WriteLineAsync($"'{HttpAdminTokenEnvironmentVariable}' environment variable is not set.");

			return 1;
		}

		Option<int> portOption = new(PortOptionName, PortOptionShortName)
		{
			DefaultValueFactory = _ => 7770,
		};

		Option<int> httpPortOption = new(HttpPortOptionName)
		{
			DefaultValueFactory = _ => 7771,
		};

		Option<LogLevel> logLevelOption = new(LogLevelOptionName, LogLevelOptionShortName)
		{
			DefaultValueFactory = _ => LogLevel.Information,
		};

		Option<FileInfo?> corsOption = new(CorsOptionName)
		{
			Description = "Path to a JSON file describing the CORS policy. If omitted, a permissive (allow any) policy is used.",
		};

		RootCommand rootCommand = new()
		{
			Options =
			{
				portOption,
				httpPortOption,
				logLevelOption,
				corsOption,
			},
		};

		rootCommand.SetAction(RootCommandAction);

		return await rootCommand.Parse(args).InvokeAsync();

		async Task<int> RootCommandAction(ParseResult parseResult, CancellationToken cancellationToken)
		{
			CorsConfiguration? corsConfiguration = null;

			FileInfo? corsConfigurationFile = parseResult.GetValue(corsOption);

			if (corsConfigurationFile is not null)
			{
				if (!corsConfigurationFile.Exists)
				{
					await Console.Error.WriteLineAsync($"CORS config file '{corsConfigurationFile.FullName}' does not exist.");

					return 1;
				}

				try
				{
					await using FileStream stream = corsConfigurationFile.OpenRead();

					JsonSerializerOptions jsonSerializerOptions = new(JsonSerializerDefaults.Web);

					corsConfiguration = await JsonSerializer.DeserializeAsync<CorsConfiguration>(stream, jsonSerializerOptions, cancellationToken);
				}
				catch (Exception exception)
				{
					await Console.Error.WriteLineAsync($"Failed to parse CORS config file '{corsConfigurationFile.FullName}': {exception.Message}");

					return 1;
				}
			}

			RelayHostOptions relayHostOptions = new()
			{
				UdpPort = parseResult.GetRequiredValue(portOption),

				HttpPort = parseResult.GetRequiredValue(httpPortOption),

				LogLevel = parseResult.GetRequiredValue(logLevelOption),

				ConnectionKey = connectionKey,

				HttpAdminToken = httpAdminToken,

				Cors = corsConfiguration,
			};

			ICancellationSignal cancellationSignal = new ConsoleCancellationSignal();

			RelayHost host = new(CreateServer, RelayHttpApi.Build, CreateLoggerFactory, cancellationSignal);

			return await host.RunAsync(relayHostOptions, cancellationToken);

			Server CreateServer(RelayHostOptions options, ILoggerFactory loggerFactory)
			{
				return new Server(options.UdpPort, options.ConnectionKey, loggerFactory.CreateLogger<Server>());
			}

			ILoggerFactory CreateLoggerFactory()
			{
				return LoggerFactory.Create(builder => builder.SetMinimumLevel(relayHostOptions.LogLevel).AddConsole());
			}
		}
	}
}
