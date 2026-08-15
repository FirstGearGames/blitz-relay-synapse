# Blitz Relay

Blitz Relay is a relay server for multiplayer game traffic, written in C# and .NET. It handles room management and data
relay between game clients, supporting both reliable and unreliable delivery modes.

## Features

- **Room-Based Relay**: Clients connect to a relay server and are assigned to rooms identified by 8-character codes.
- **Two Room Types**:
    - *Ephemeral*: Created by a host client and destroyed when the host disconnects.
    - *Persistent Reserved*: Created via the HTTP API, survive host disconnection, and support automatic host promotion
      to remaining clients.
- **Host Migration**: Persistent rooms automatically promote the longest-connected client to host when the host
  disconnects.
- **Dual Protocol Support**: UDP for low-latency game traffic relay, HTTP for administrative room management.
- **Configurable Delivery**: Game data can be sent reliably or unreliably via separate channels.
- **Hardened UDP Transport**: [SynapseSocket](https://github.com/FirstGearGames/SynapseSocket) provides the reliable and
  unreliable channels, payload segmentation, and a security pipeline that rate-limits, drops, and blacklists abusive
  peers before their payloads are ever copied.
- **Simple Client Integration**: A minimal binary protocol using the `BlitzRelay.Protocol` library.
- **AOT Published**: The server is compiled ahead-of-time for fast startup and minimal memory usage.

## Architecture

The server consists of two main components:

1. **UDP Relay Server** (port 7770 by default): Handles room management and game traffic relay between connected peers.
2. **HTTP Admin API** (port 7771 by default): Provides REST endpoints for creating, listing, and deleting rooms.

### Room Lifecycle

- A host client registers with the relay and receives a room code.
- Other clients join using the room code and receive a virtual client ID.
- The host sends data to specific clients or broadcasts to all.
- When the host of a persistent room disconnects, the server promotes the next client in join order to host.

## Installation

Download the latest release from the [releases page](https://github.com/abdelfattahradwan/blitz-relay/releases) or build
from source.

The UDP transport is consumed as a git submodule, so a source build needs it checked out:

```bash
git clone --recurse-submodules <repository-url>
```

If the repository is already cloned, run `git submodule update --init --recursive` once before building.

## Usage

### Prerequisites

- .NET 10.0 runtime (or use the `standalone` or `native` pre-built binaries)

### Running the Server

Set the required environment variables before starting:

```bash
export BLITZ_RELAY_CONNECTION_KEY="your-connection-key"
export BLITZ_RELAY_HTTP_ADMIN_TOKEN="your-admin-token"
```

```powershell
$env:BLITZ_RELAY_CONNECTION_KEY = "your-connection-key"
$env:BLITZ_RELAY_HTTP_ADMIN_TOKEN = "your-admin-token"
```

Then run the server:

```bash
BlitzRelay [--port <udp-port>] [--http-port <http-port>] [--log-level <level>] [--cors <config-file>]
```

**Command-Line Options**

| Option        | Short | Default     | Description                                                         |
|---------------|-------|-------------|---------------------------------------------------------------------|
| `--port`      | `-p`  | 7770        | UDP port for relay traffic                                          |
| `--http-port` |       | 7771        | HTTP port for admin API                                             |
| `--log-level` | `-l`  | Information | Logging level (Trace, Debug, Information, Warning, Error, Critical) |
| `--cors`      |       |             | Optional path to a JSON file describing the HTTP API CORS policy    |

### CORS

If you do not pass `--cors`, Blitz Relay uses a permissive CORS policy for the HTTP API:

- Any origin is allowed.
- Any HTTP method is allowed.
- Any request header is allowed.
- Credentials are not enabled.

This default keeps setup simple for local tools, browser dashboards, and hosted admin panels. The HTTP API still
requires
the configured `Bearer` token for protected endpoints.

If you need to restrict browser access, create a JSON file and pass it with `--cors`:

```bash
BlitzRelay --cors ./cors.json
```

Example `cors.json`:

```json
{
  "allowedOrigins": [
    "https://admin.example.com"
  ],
  "allowedMethods": [
    "GET",
    "POST",
    "PATCH",
    "DELETE"
  ],
  "allowedHeaders": [
    "Authorization",
    "Content-Type"
  ],
  "allowCredentials": false
}
```

All fields are optional. If `allowedOrigins`, `allowedMethods`, or `allowedHeaders` is omitted or empty, that part of
the
policy defaults to allowing any value. Set `allowCredentials` to `true` only when you list specific origins; browsers do
not allow credentials with a wildcard origin.

### HTTP Admin API

Most endpoints require a `Bearer` token in the `Authorization` header, matching the `BLITZ_RELAY_HTTP_ADMIN_TOKEN`
environment variable. `DELETE /rooms/{roomCode}`, `PATCH /rooms/{roomCode}`, and
`DELETE /rooms/{roomCode}/clients/{virtualClientId}`
additionally accept the room's host token.

| Method | Endpoint                                      | Description                               |
|--------|-----------------------------------------------|-------------------------------------------|
| GET    | `/health`                                     | Health check (no authentication)          |
| POST   | `/rooms`                                      | Create a persistent room                  |
| GET    | `/rooms`                                      | List all rooms                            |
| GET    | `/rooms/{roomCode}`                           | Get details of a specific room            |
| DELETE | `/rooms/{roomCode}`                           | Delete a room                             |
| PATCH  | `/rooms/{roomCode}`                           | Update a room's display name and metadata |
| DELETE | `/rooms/{roomCode}/clients/{virtualClientId}` | Kick a client from a room                 |

**Create Room Request**

```json
{
  "maximumClients": 8,
  "displayName": "My Game Room",
  "isPublic": true,
  "metadata": {
    "key": "value"
  }
}
```

**Patch Room Request**

```json
{
  "displayName": "Updated Room Name",
  "metadataToAdd": {
    "mode": "ranked"
  },
  "metadataToRemove": [
    "oldKey"
  ]
}
```

**Room Response**

```json
{
  "code": "ABC12345",
  "kind": "PersistentReserved",
  "isPublic": true,
  "displayName": "My Game Room",
  "maximumClients": 8,
  "connectedClientCount": 0,
  "hasHost": false,
  "hasPendingHostClaim": false,
  "metadata": {}
}
```

For the complete, interactive API reference, see the [OpenAPI specification](docs/relay-http-api/openapi.yaml) or
the [Redoc documentation](docs/relay-http-api/redoc-static.html).

### Client Integration

Clients talk to the relay over [SynapseSocket](https://github.com/FirstGearGames/SynapseSocket) and frame their messages
with the `BlitzRelay.Protocol` library:

1. Connect a `SynapseManager` to the relay's UDP port.
2. Send an `Authenticate` message carrying the connection key, reliably, as the very first message. The relay answers
   silence on success, an `Error` with `InvalidConnectionKey` on failure, and ignores every other message until a peer
   has authenticated. A peer that has not authenticated within ten seconds is disconnected.
3. To host: send a `HostRegister` message with the maximum number of clients.
4. To join: send a `ClientJoin` message with the room code.
5. Send game data using `HostData` (from host) or `ClientData` (from clients). Send a message on the SynapseSocket
   channel that matches its `GameChannel`: reliable game data on the reliable channel, unreliable on the unreliable one.
   The relay drops any message whose declared channel disagrees with the channel it arrived on.
6. Handle incoming `Connected`, `Disconnected`, and `Data` messages.

Because `Authenticate` and the message that follows it are both reliable and therefore ordered, a client can send them
back to back without waiting in between.

See the `BlitzRelay.Protocol` library for the full message codec API.

### Transport Limits

| Limit                       | Value                | Notes                                                            |
|-----------------------------|----------------------|------------------------------------------------------------------|
| MTU                         | 1200 bytes           | Payloads above this are segmented on the reliable channel        |
| Maximum datagram            | 1400 bytes           | A larger datagram raises an oversized violation                  |
| Maximum unreliable payload  | 1197 bytes           | Unreliable game data is never segmented; larger payloads are dropped |
| Maximum reliable payload    | 64 KB                | Larger payloads are rejected rather than relayed                 |
| Rate limit                  | 2000 packets/second, 4 MB/second | Per peer; a peer over the limit is dropped and blacklisted |
| Keep-alive / timeout        | 2 s / 30 s           | An idle peer is disconnected after the timeout                   |

## Configuration Reference

| Environment Variable           | Description                                                       |
|--------------------------------|-------------------------------------------------------------------|
| `BLITZ_RELAY_CONNECTION_KEY`   | Key each UDP peer presents in its `Authenticate` message (required) |
| `BLITZ_RELAY_HTTP_ADMIN_TOKEN` | Token for HTTP admin API authentication (required)                |

## Acknowledgements

Blitz Relay uses [SynapseSocket](https://github.com/FirstGearGames/SynapseSocket) by FirstGearGames for UDP networking.

## License

MIT License. See [LICENSE](LICENSE) for details.

Copyright 2026 Abdelfattah Radwan
