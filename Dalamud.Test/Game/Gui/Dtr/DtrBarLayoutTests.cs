using Dalamud.Configuration.Internal;
using Dalamud.Game.Gui.Dtr;

using Newtonsoft.Json;

using Xunit;

namespace Dalamud.Test.Game.Gui.Dtr
{
    public class DtrBarLayoutTests
    {
        [Fact]
        public void EverythingFitsStaysOnOneRow()
        {
            var cursor = default(DtrRowCursor);
            var offsets = PlaceAll(ref cursor, 100, out var rows, 30, 30, 30);

            Assert.Equal(new float[] { 0, 30, 60 }, offsets);
            Assert.Equal(new[] { 0, 0, 0 }, rows);
            Assert.Equal(1, cursor.RowCount);
            Assert.Equal(90, cursor.FirstRowUsed);
        }

        [Fact]
        public void ExactFitDoesNotWrap()
        {
            var cursor = default(DtrRowCursor);
            PlaceAll(ref cursor, 90, out var rows, 30, 30, 30);

            Assert.Equal(new[] { 0, 0, 0 }, rows);
        }

        [Fact]
        public void OverflowStartsNewRowFromTheRowStart()
        {
            var cursor = default(DtrRowCursor);
            var offsets = PlaceAll(ref cursor, 70, out var rows, 30, 30, 30, 30, 30);

            Assert.Equal(new[] { 0, 0, 1, 1, 2 }, rows);
            Assert.Equal(new float[] { 0, 30, 0, 30, 0 }, offsets);
            Assert.Equal(3, cursor.RowCount);
            Assert.Equal(60, cursor.FirstRowUsed);
        }

        [Fact]
        public void EntryWiderThanLimitGetsItsOwnRow()
        {
            var cursor = default(DtrRowCursor);
            var offsets = PlaceAll(ref cursor, 50, out var rows, 20, 200, 20);

            Assert.Equal(new[] { 0, 1, 2 }, rows);
            Assert.Equal(new float[] { 0, 0, 0 }, offsets);
        }

        [Fact]
        public void OversizedFirstEntryStaysOnFirstRow()
        {
            var cursor = default(DtrRowCursor);
            PlaceAll(ref cursor, 50, out var rows, 200, 20);

            Assert.Equal(new[] { 0, 1 }, rows);
            Assert.Equal(200, cursor.FirstRowUsed);
        }

        [Theory]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NaN)]
        [InlineData(0f)]
        [InlineData(-5f)]
        public void NoUsableLimitNeverWraps(float limit)
        {
            var cursor = default(DtrRowCursor);
            PlaceAll(ref cursor, limit, out var rows, 300, 300, 300);

            Assert.Equal(new[] { 0, 0, 0 }, rows);
            Assert.Equal(900, cursor.FirstRowUsed);
        }

        [Fact]
        public void NothingPlacedHasNoRows()
        {
            Assert.Equal(0, default(DtrRowCursor).RowCount);
        }

        [Theory]
        [InlineData(500, 2000, 500)]
        [InlineData(500, 300, 300)]
        [InlineData(0, 300, 300)]
        [InlineData(500, float.NaN, 500)]
        [InlineData(500, 10, 500)]
        [InlineData(-1, float.NaN, float.PositiveInfinity)]
        [InlineData(0, 0, float.PositiveInfinity)]
        public void RowLimitTakesTheTighterUsableBound(float configured, float room, float expected)
        {
            Assert.Equal(expected, DtrBarLayout.RowLimit(configured, room));
        }

        [Theory]
        [InlineData(20, 1080, false)]
        [InlineData(1050, 1080, true)]
        [InlineData(1050, 0, false)]
        [InlineData(float.NaN, 1080, false)]
        public void RowsGoUpOnlyInTheLowerHalf(float centerY, float screenHeight, bool expected)
        {
            Assert.Equal(expected, DtrBarLayout.WrapUpwards(centerY, screenHeight));
        }

        [Fact]
        public void MissingConfigKeysDefaultToWrapping()
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
            };

            var config = JsonConvert.DeserializeObject<DalamudConfiguration>("{\"DtrSpacing\": 3}", settings)!;

            Assert.True(config.DtrWrapWhenTooLong);
            Assert.Equal(500, config.DtrWrapWidth);
            Assert.Equal(3, config.DtrSpacing);
        }

        private static float[] PlaceAll(ref DtrRowCursor cursor, float limit, out int[] rows, params float[] widths)
        {
            var offsets = new float[widths.Length];
            rows = new int[widths.Length];
            for (var i = 0; i < widths.Length; i++)
            {
                offsets[i] = DtrBarLayout.Place(ref cursor, widths[i], limit);
                rows[i] = cursor.Row;
            }

            return offsets;
        }
    }
}
