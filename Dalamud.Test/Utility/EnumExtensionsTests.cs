using System;
using System.ComponentModel;

using Dalamud.Utility;

using Xunit;

namespace Dalamud.Test.Utility
{
    public class EnumExtensionsTests
    {
        private enum Probe
        {
            [Description("first")]
            First,

            Second,

            [Tag("a")]
            [Tag("b")]
            Twice,

            [Obsolete("gone")]
            Old,
        }

        [Fact]
        public void GetAttributeAnswersAsBeforeAndReusesTheAttribute()
        {
            var first = Probe.First.GetAttribute<DescriptionAttribute>();
            Assert.Equal("first", first?.Description);
            Assert.Same(first, Probe.First.GetAttribute<DescriptionAttribute>());

            Assert.Null(Probe.Second.GetAttribute<DescriptionAttribute>());
            Assert.Null(Probe.Second.GetAttribute<DescriptionAttribute>());
            Assert.Null(((Probe)99).GetAttribute<DescriptionAttribute>());
            Assert.Null(Probe.First.GetAttribute<TagAttribute>());

            Assert.True(((Probe)3).IsObsolete()); // Probe.Old, named without the obsolete warning.
            Assert.False(Probe.First.IsObsolete());
        }

        [Fact]
        public void SeveralMatchingAttributesStillThrowEveryTime()
        {
            Assert.Throws<InvalidOperationException>(() => Probe.Twice.GetAttribute<TagAttribute>());
            Assert.Throws<InvalidOperationException>(() => Probe.Twice.GetAttribute<TagAttribute>());
            Assert.Equal(2, System.Linq.Enumerable.Count(Probe.Twice.GetAttributes<TagAttribute>()));
        }

        [AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
        private sealed class TagAttribute(string name) : Attribute
        {
            public string Name { get; } = name;
        }
    }
}
