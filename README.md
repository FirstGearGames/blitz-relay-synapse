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
BlitzRelay [--port <udp-port>] [--http-port <http-port>] [--log-level <level>]
```

**Command-Line Options**

| Option        | Short | Default     | Description                                                         |
|---------------|-------|-------------|---------------------------------------------------------------------|
| `--port`      | `-p`  | 7770        | UDP port for relay traffic                                          |
| `--http-port` |       | 7771        | HTTP port for admin API                                             |
| `--log-level` | `-l`  | Information | Logging level (Trace, Debug, Information, Warning, Error, Critical) |

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

Clients use the `BlitzRelay.Protocol` library to communicate with the relay server:

1. Connect to the relay using the UDP port and connection key.
2. To host: send a `HostRegister` message with the maximum number of clients.
3. To join: send a `ClientJoin` message with the room code.
4. Send game data using `HostData` (from host) or `ClientData` (from clients).
5. Handle incoming `Connected`, `Disconnected`, and `Data` messages.

See the `BlitzRelay.Protocol` library for the full message codec API.

## Configuration Reference

| Environment Variable           | Description                                         |
|--------------------------------|-----------------------------------------------------|
| `BLITZ_RELAY_CONNECTION_KEY`   | Key used to authenticate UDP connections (required) |
| `BLITZ_RELAY_HTTP_ADMIN_TOKEN` | Token for HTTP admin API authentication (required)  |

## Acknowledgements

Blitz Relay uses [LiteNetLib](https://github.com/RevenantX/LiteNetLib) by RevenantX for UDP networking.

## License

MIT License. See [LICENSE](LICENSE) for details.

Copyright 2026 Abdelfattah Radwan
