using Amazon.S3.Model;
using Resourcefull.ObjectStorage;
using Resourcefull.S3;
using Resourcefull.Tests.ObjectStorage;

namespace Resourcefull.Tests.S3;

[Collection(S3Collection.Name)]
public sealed class S3SessionJoinTests(S3Fixture fixture) : ObjectStoreSessionJoinTests
{
    protected override void SkipIfUnavailable() => fixture.SkipIfUnavailable();

    private protected override IObjectStore NewStore() => new S3ObjectStore(fixture.Client, fixture.BucketName);

    protected override async Task<IReadOnlyList<string>> ListKeysAsync(string prefix)
    {
        ListObjectsV2Response listed = await fixture.Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = fixture.BucketName, Prefix = prefix
        });
        return [.. (listed.S3Objects ?? []).Select(o => o.Key)];
    }
}
