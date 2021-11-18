# Git Core

Essentials to implement a custom managed Git client for incremental updates of huge downloadable contents (DLC) published to a Git repository.

A client app can request with a commit or tree hash for a pack of wanted blobs and trees over Git Smart HTTP with the .NET built-in `HttpClient` instead of a full-blown Git client with a whole bunch of native executables.

This project begins with retrieving a Git pack using only efficient .NET 6.0 APIs. Further contribution is welcome.

## Example: discovery of references

```cs
using FlyByWireless.GitCore;

using HttpClient client = new()
{
    BaseAddress = new("https://github.com/wegylexy/git-core.git/")
};
var upa = client.GetUploadPackAsync();
await foreach (var p in upa)
{
    var r = p.Key;
    if (r.StartsWith("refs/heads/"))
    {
        r = string.Concat("branch ", r.AsSpan(11));
    }
    else if (r.StartsWith("refs/tags/"))
    {
        r = string.Concat("tag ", r.AsSpan(10));
    }
    Console.WriteLine($"{r} -> commit {p.Value.ToHexString()}");
}
```

## Example: shallow-fetch of a tree

Git remote capable of `allow-reachable-sha1-in-want`, `shallow`, and `object-format=sha1`:
- `https://github.com/wegylexy/git-core.git/`  
  (trailing `/` is required)

Wanted objects:
- `tree d56c74a8ae5d81ddfbebce18eea3c791fcea5e2d`

Local existing objects:
- `blob 1ff0c423042b46cb1d617b81efb715defbe8054d`
- `blob 9491a2fda28342ab358eaf234e1afe0c07a53d62`

```cs
using FlyByWireless.GitCore;

using HttpClient client = new()
{
    BaseAddress = new("https://github.com/wegylexy/git-core.git/")
};
using var response = await client.PostUploadPackAsync(new(
    want: new ReadOnlyMemory<byte>[]
    {
        "d56c74a8ae5d81ddfbebce18eea3c791fcea5e2d".ParseHex()
    },
    depth: 1,
    have: new ReadOnlyMemory<byte>[]
    {
        "1ff0c423042b46cb1d617b81efb715defbe8054d".ParseHex(),
        "9491a2fda28342ab358eaf234e1afe0c07a53d62".ParseHex()
    }
));
await foreach (var uo in response.Pack) // for each unpacked object
{
    var co = uo; // delta will re-assign current object
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
        case ObjectType.Commit:
            {
                var treeHash = co.ToCommitContent().Tree;
                // TODO: cache root tree
            }
            break;
    }
}
// TODO: delete files that is not in any tree
```