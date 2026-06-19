namespace BlitzRelay.Hosting;

public sealed record CorsConfiguration
(
	string[]? AllowedOrigins = null,
	string[]? AllowedMethods = null,
	string[]? AllowedHeaders = null,
	bool AllowCredentials = false
);
