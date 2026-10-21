using System;

using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.Internal;

using Xunit;

namespace Dalamud.Test.Interface.Textures
{
    public class TextureUploadLengthTests
    {
        private const int R8G8B8A8Unorm = 28;
        private const int Bc1Unorm = 71;
        private const int R8G8B8G8Unorm = 68; // Not a format RawImageSpecification knows.

        [Fact]
        public void UploadBytesCountsEveryRowAPitchApart()
        {
            Assert.Equal(48, RawImageSpecification.Rgba32(4, 3).UploadBytes);
            Assert.Equal(60, new RawImageSpecification(4, 3, R8G8B8A8Unorm, 20).UploadBytes);
            Assert.Equal(32, new RawImageSpecification(4, 3, R8G8B8A8Unorm, 8).UploadBytes);
            Assert.Equal(32, new RawImageSpecification(5, 5, Bc1Unorm).UploadBytes);
            Assert.Equal(8, new RawImageSpecification(4, 4, Bc1Unorm, 0).UploadBytes);

            Assert.Equal(-1, RawImageSpecification.Rgba32(0, 3).UploadBytes);
            Assert.Equal(-1, (RawImageSpecification.Rgba32(4, 3) with { Pitch = -2 }).UploadBytes);
            Assert.Equal(-1, new RawImageSpecification(4, 3, R8G8B8G8Unorm, 16).UploadBytes);
        }

        [Fact]
        public void EnoughDataIsPassedThrough()
        {
            var data = new byte[48];
            var result = TextureManager.EnsureUploadLength(RawImageSpecification.Rgba32(4, 3), data);
            Assert.True(result == data);
        }

        [Fact]
        public void ShortDataIsCopiedIntoAZeroFilledBuffer()
        {
            // A single-pixel placeholder given one byte for four.
            var result = TextureManager.EnsureUploadLength(RawImageSpecification.Rgba32(1, 1), new byte[] { 7 });
            Assert.Equal(new byte[] { 7, 0, 0, 0 }, result.ToArray());

            // A sub-rectangle whose last row stops at the row's end rather than at the pitch.
            var tail = new byte[(2 * 20) + 16];
            tail[^1] = 9;
            var padded = TextureManager.EnsureUploadLength(new RawImageSpecification(4, 3, R8G8B8A8Unorm, 20), tail);
            Assert.Equal(60, padded.Length);
            Assert.Equal(9, padded[55]);
            Assert.Equal(0, padded[59]);
        }

        [Fact]
        public void UncheckableOrFarTooShortThrows()
        {
            Assert.Throws<ArgumentException>(() => { TextureManager.EnsureUploadLength(RawImageSpecification.Rgba32(0, 1), new byte[4]); });
            Assert.Throws<ArgumentException>(() => { TextureManager.EnsureUploadLength(RawImageSpecification.Rgba32(4, 3) with { Pitch = -2 }, new byte[64]); });
            Assert.Throws<ArgumentException>(() => { TextureManager.EnsureUploadLength(new RawImageSpecification(4, 3, R8G8B8G8Unorm, 16), new byte[64]); });
            Assert.Throws<ArgumentException>(() => { TextureManager.EnsureUploadLength(RawImageSpecification.Rgba32(8192, 8192), new byte[4]); });
        }
    }
}
