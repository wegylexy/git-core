# Git Core

Essentials to implement a custom managed Git client for incremental updates of huge downloadable contents (DLC) published to a Git repository.

Suppose the output of `git ls-tree -rl` is published. A client app can then diff locally. Once wanted blob object IDs are determined, a pack of wanted blobs may be requested with Git Smart HTTP via `HttpClient` instead of a full-blown Git client.

This project begins with unpacking a Git pack stream using only efficient .NET 6.0 APIs. Further contribution is welcome.