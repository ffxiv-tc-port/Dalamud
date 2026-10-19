#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Dalamud.Interface.GameFonts;

using Xunit;

namespace Dalamud.Test.Interface.GameFonts
{
    // GameFontStyle keys the prebaked font dictionaries, looked up on every push of a game font handle.
    // Its equality must stay what the default struct equality was: each instance field compared with its own Equals.
    public class GameFontStyleEqualityTests
    {
        private static readonly float[] Values =
        [
            0f, -0f, float.NaN, BitConverter.Int32BitsToSingle(unchecked((int)0xFFC00001)), 1f, 2.5f,
            float.PositiveInfinity, float.NegativeInfinity, float.Epsilon,
        ];

        private static readonly GameFontFamilyAndSize[] Families =
            [GameFontFamilyAndSize.Undefined, GameFontFamilyAndSize.Axis12, GameFontFamilyAndSize.Axis36];

        private static FieldInfo[] InstanceFields =>
            typeof(GameFontStyle).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        [Fact]
        public void TheInstanceFieldsAreTheFourThatEqualsCompares()
        {
            // A new field must be added to Equals and GetHashCode as well.
            Assert.Equal(
                new[] { "FamilyAndSize", "SizePx", "SkewStrength", "Weight" },
                InstanceFields.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void EqualsGivesTheSameResultAsTheFieldwiseDefault()
        {
            var styles = AllStyles().ToArray();
            var fields = InstanceFields;
            foreach (var a in styles)
            {
                foreach (var b in styles)
                {
                    var expected = fields.All(f => Equals(f.GetValue(a), f.GetValue(b)));
                    Assert.Equal(expected, a.Equals(b));
                    Assert.Equal(expected, a.Equals((object)b));
                    Assert.Equal(expected, EqualityComparer<GameFontStyle>.Default.Equals(a, b));
                    if (expected)
                        Assert.Equal(a.GetHashCode(), b.GetHashCode());
                }
            }
        }

        [Fact]
        public void EqualsRejectsOtherTypesAndNull()
        {
            var style = new GameFontStyle(GameFontFamilyAndSize.Axis12);
            Assert.False(style.Equals(null!));
            Assert.False(style.Equals(GameFontFamilyAndSize.Axis12));
        }

        [Fact]
        public void DictionaryLookupsDoNotAllocate()
        {
            var dict = AllStyles().Take(64).Distinct().ToDictionary(x => x, x => 1);
            var keys = dict.Keys.ToArray();
            var probe = new GameFontStyle(GameFontFamilyAndSize.Axis36) { Weight = 3f };

            var hits = Lookups(dict, keys, probe, 2_000);
            var before = AllocatedBytes.Start();
            hits += Lookups(dict, keys, probe, 20_000);
            var allocated = AllocatedBytes.Since(before);

            Assert.True(hits > 0);
            Assert.Equal(0, allocated);
        }

        private static int Lookups(Dictionary<GameFontStyle, int> dict, GameFontStyle[] keys, GameFontStyle miss, int rounds)
        {
            var hits = 0;
            for (var i = 0; i < rounds; i++)
            {
                if (dict.TryGetValue(keys[i % keys.Length], out var v))
                    hits += v;
                if (dict.TryGetValue(miss, out _))
                    hits--;
            }

            return hits;
        }

        private static IEnumerable<GameFontStyle> AllStyles()
        {
            foreach (var family in Families)
            {
                foreach (var size in Values)
                {
                    foreach (var weight in Values)
                    {
                        foreach (var skew in Values)
                        {
                            yield return new GameFontStyle
                            {
                                FamilyAndSize = family, SizePx = size, Weight = weight, SkewStrength = skew,
                            };
                        }
                    }
                }
            }
        }
    }
}
