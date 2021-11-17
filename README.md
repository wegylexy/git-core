# Git Core

Essentials to implement a custom managed Git client for incremental updates of huge downloadable contents (DLC) published to a Git repository.

A client app can request with a commit or tree hash for a pack of wanted blobs and trees over Git Smart HTTP with the .NET built-in `HttpClient` instead of a full-blown Git client with a whole bunch of native executables.

This project begins with retrieving a Git pack using only efficient .NET 6.0 APIs. Further contribution is welcome.

## Example

Git Remote:
- `https://github.com/wegylexy/git-core.git/`  
  (trailing `/` is required)

Wanted Tree:
- `d56c74a8ae5d81ddfbebce18eea3c791fcea5e2d`

Local Existing Blobs:
- `1ff0c423042b46cb1d617b81efb715defbe8054d`
- `9491a2fda28342ab358eaf234e1afe0c07a53d62`

```cs
using FlyByWireless.GitCore;

using HttpClient client = new()
{
    BaseAddress = new("https://github.com/wegylexy/git-core.git/")
};
using var response = await client.PostUploadPackAsync(new(
    new ReadOnlyMemory<byte>[] { "d56c74a8ae5d81ddfbebce18eea3c791fcea5e2d".ParseHex() },
    new ReadOnlyMemory<byte>[]
    {
        "1ff0c423042b46cb1d617b81efb715defbe8054d".ParseHex(),
        "9491a2fda28342ab358eaf234e1afe0c07a53d62".ParseHex()
    }
));
await foreach (var uo in response.Pack)
{
    var co = uo;
Triage:
    switch (uo.Type)
    {
        case ObjectType.Blob:
            // TODO: save for every matching tree entry
            break;
        case ObjectType.Tree:
            await foreach (var entry in co.AsTree())
            {
                switch (entry.Type)
                {
                    case TreeEntryType.Blob:
                        // TODO: cache file name
                        break;
                    case TreeEntryType.Tree:
                        // TODO: create directory
                        break;
                }
            }
            break;
        case ObjectType.ReferenceDelta:
            {
                UnpackedObject baseObject;
                // TODO: load base object
                co = co.Delta(baseObject);
                goto Triage;
            }
            break;
    }
}
// TODO: delete files that is not in any tree
```