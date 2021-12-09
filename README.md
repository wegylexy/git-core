# Git Core

Essentials to implement a custom managed Git client for incremental updates of huge downloadable contents (DLC) published to a Git repository.

A client app can request with a commit or tree hash for a pack of wanted blobs and trees over Git Smart HTTP with the .NET built-in `HttpClient` instead of a full-blown Git client with a whole bunch of native executables.

This project begins with retrieving a Git pack using only efficient .NET 6.0 APIs. Further contribution is welcome.

## Example: discovery of references

```cs
using FlyByWireless.GitCore;

using HttpClient client = new(new AuthorizingHttpClientHandler());
var upa = client.GetUploadPackAsync(new("https://user:token@github.com/wegylexy/git-core.git/"));
await foreach (var p in upa)
{
    var r = p.Key;
    if (r.StartsWith("refs/heads/"))
    {
        Console.WriteLine($"branch {r.AsSpan(11)} -> commit {p.Value.ToHexString()}");
    }
    else if (r.StartsWith("refs/tags/") && r.EndsWith("^{}"))
    {
        Console.WriteLine($"tag {r.AsSpan()[10..^3]} -> commit {p.Value.ToHexString()}");
    }
}
```

## Example: shallow-fetch of a tree

Git remote capable of `allow-reachable-sha1-in-want`, `shallow`, and `object-format=sha1`:
- Remote URL: `https://github.com/wegylexy/git-core.git/` (trailing `/` is required)
- Username: `user`
- Token: `token`

Local:
- `tree f0d3a70ceaa69fb70811f58254dc738e0f939eac`
  - `100644 blob 1ff0c423042b46cb1d617b81efb715defbe8054d	.gitattributes`
  - `100644 blob 9491a2fda28342ab358eaf234e1afe0c07a53d62	.gitignore`

Remote:
- `tree d56c74a8ae5d81ddfbebce18eea3c791fcea5e2d`
  - `100644 blob 1ff0c423042b46cb1d617b81efb715defbe8054d	.gitattributes` (common)
  - `100644 blob 9491a2fda28342ab358eaf234e1afe0c07a53d62	.gitignore` (common)
  - `040000 tree 6aaf05e6bf2af00e0574cc021ff72b29386e5eb1	GitCore.Tests`
    - ...
  - `100644 blob a03fb82afc9b189ffc24de772720456de61aee5e	GitCore.sln`
  - `040000 tree ccd51a743e683abbb13481be1c0384d9ce837d0c	GitCore`
    - ...
  - `100644 blob d2489a28362500baaeefe5121a20fbe9b9145ead	LICENSE`
  - `100644 blob c9b283eddb6d8926508d645b15b671bc2802c232	README.md`

Default capabilities includes `thin-pack`. The 2 blobs that local already has will not be packed.

```cs
using FlyByWireless.GitCore;

using HttpClient client = new(new AuthorizingHttpClientHandler());
using var response = await client.PostUploadPackAsync(new("https://user:token@github.com/wegylexy/git-core.git/"),
    new
    (
        want: new ReadOnlyMemory<byte>[] { "d56c74a8ae5d81ddfbebce18eea3c791fcea5e2d".ParseHex() },
        depth: 1,
        have: new ReadOnlyMemory<byte>[] { "f0d3a70ceaa69fb70811f58254dc738e0f939eac".ParseHex() }
    )
    {
        Capabilities = { "include-tag" }
    }
);
await foreach (var uo in response.Pack) // for each unpacked object
{
    var co = uo; // delta will re-assign current object
Triage:
    switch (co.Type)
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
        case ObjectType.Tag when co.Type == ObjectType.Commit:
            {
                var tag = co.ToTagContent();
                var commitHash = tag.Object;
                var name = tag.Name;
                var message = tag.Message;
                // TODO: associate tag message to commit
            }
            break;
    }
}
// TODO: delete files that is not in any tree
```