# Factions

NPC-faction economy for Space Engineers dedicated servers: factions with treasuries, territory,
production chains, fleets and diplomacy, driven by a simulation that runs outside the game world
and is projected into it when players are near.

## Components

| Path | What it is |
|---|---|
| `src/Factions.Core` | The simulation engine (netstandard2.0, no Space Engineers references) |
| `src/Factions` | Torch server plugin hosting the engine (net48) |
| `src/FactionsUI` | Space Engineers client plugin, loaded by Pulsar |
| `src/Factions.Wire`, `src/Factions.Shared` | Source shared by the server and client plugins |
| `src/Factions.Api`, `src/Factions.Dashboard` | Web dashboard backend (net9) and frontend (React) |
| `src/Factions.Sim` | Offline simulation and balancing tool (net9) |
| `tests/Factions.Core.Tests` | Engine tests (xUnit) |

## Installing the client plugin

Factions UI installs through [Pulsar](https://github.com/SpaceGT/Pulsar) straight from this repository:
see [Installing Factions UI](docs/install-client.md). Client and server must speak the same wire
protocol, which each [release](https://github.com/Arkil81/Factions/releases) states.

## Building

```
dotnet build src/Factions.Core/Factions.Core.csproj -c Release
dotnet test  tests/Factions.Core.Tests/
```

The server and client plugins need a local Space Engineers / Torch install to compile against.
`build.ps1` packages the Torch plugin.

## License

The code is GPL-3.0; see [LICENSE](LICENSE). The traffic-control voice clips are not covered by
it and carry their voices' own terms; see [ASSETS-LICENSE.md](ASSETS-LICENSE.md).
