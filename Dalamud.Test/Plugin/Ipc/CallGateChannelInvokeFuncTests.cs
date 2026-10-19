#nullable enable

using System;
using System.Reflection;

using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Ipc.Internal;

using Newtonsoft.Json.Linq;

using Xunit;

namespace Dalamud.Test.Plugin.Ipc
{
    // InvokeFunc calls a parameterless provider of the very same return type directly. Everything a caller could
    // observe before must stay: which error, when the provider has already run, and what goes through JSON.
    public class CallGateChannelInvokeFuncTests
    {
        private static readonly object?[] NoArgs = [];

        [Fact]
        public void WithoutAProviderItIsNotReady()
        {
            var channel = new CallGateChannel("Test.NotReady");

            Assert.Throws<IpcNotReadyError>(() => channel.InvokeFunc<bool>(NoArgs));
            Assert.Throws<IpcNotReadyError>(() => channel.InvokeFunc<bool>(null));

            channel.Func = new Func<bool>(() => true);
            Assert.True(channel.InvokeFunc<bool>(NoArgs));
            channel.Func = null;
            Assert.Throws<IpcNotReadyError>(() => channel.InvokeFunc<bool>(NoArgs));
        }

        [Fact]
        public void TheSameReturnTypeComesBackAsIs()
        {
            var flag = false;
            var text = new string('x', 3);
            var payload = new Payload { Number = 7, Text = "seven" };
            var boolChannel = new CallGateChannel("Test.Bool") { Func = new Func<bool>(() => flag) };
            var intChannel = new CallGateChannel("Test.Int") { Func = new Func<int>(() => 42) };
            var tupleChannel = new CallGateChannel("Test.Tuple") { Func = new Func<(int A, long B)>(() => (1, 2L)) };
            var stringChannel = new CallGateChannel("Test.String") { Func = new Func<string>(() => text) };
            var payloadChannel = new CallGateChannel("Test.Payload") { Func = new Func<Payload>(() => payload) };

            Assert.False(boolChannel.InvokeFunc<bool>(NoArgs));
            flag = true;
            Assert.True(boolChannel.InvokeFunc<bool>(null));
            Assert.Equal(42, intChannel.InvokeFunc<int>(NoArgs));
            Assert.Equal((1, 2L), tupleChannel.InvokeFunc<(int A, long B)>(NoArgs));
            Assert.Same(text, stringChannel.InvokeFunc<string>(NoArgs));
            Assert.Same(payload, payloadChannel.InvokeFunc<Payload>(NoArgs));
        }

        [Fact]
        public void AnotherReturnTypeStillGoesThroughJsonAfterTheProviderRan()
        {
            var calls = 0;
            var payload = new Payload { Number = 7, Text = "seven" };
            var intChannel = new CallGateChannel("Test.Widen") { Func = new Func<int>(() => 5) };
            var payloadChannel = new CallGateChannel("Test.Mirror")
            {
                Func = new Func<Payload>(() =>
                {
                    calls++;
                    return payload;
                }),
            };

            Assert.Equal(5L, intChannel.InvokeFunc<long>(NoArgs));

            var mirror = payloadChannel.InvokeFunc<PayloadMirror>(NoArgs);
            Assert.Equal(7, mirror.Number);
            Assert.Equal("seven", mirror.Text);

            // Func<Payload> is also a Func<object>, yet a caller asking for object has always received the JSON form.
            var asObject = payloadChannel.InvokeFunc<object>(NoArgs);
            Assert.IsType<JObject>(asObject);
            Assert.Equal(7, ((JObject)asObject)["Number"]!.Value<int>());

            calls = 0;
            Assert.Throws<IpcTypeMismatchError>(() => payloadChannel.InvokeFunc<int>(NoArgs));
            Assert.Equal(1, calls);
        }

        [Fact]
        public void AMethodReturningMoreThanItsDelegateSaysIsConverted()
        {
            // The delegate is a Func<object>, the method behind it returns string: the declared type decides nothing.
            var channel = new CallGateChannel("Test.Covariant") { Func = new Func<object>(ReturnsPayloadAsString) };

            Assert.Equal("payload", channel.InvokeFunc<string>(NoArgs));
            Assert.Equal("payload", Assert.IsType<string>(channel.InvokeFunc<object>(NoArgs)));
        }

        [Fact]
        public void ANullFromANullableProviderStillFailsTheSameWayForANonNullableCaller()
        {
            var channel = new CallGateChannel("Test.Nullable") { Func = new Func<bool?>(() => null) };

            Assert.Null(channel.InvokeFunc<bool?>(NoArgs));
            Assert.Throws<NullReferenceException>(() => channel.InvokeFunc<bool>(NoArgs));
        }

        [Fact]
        public void AThrowingProviderIsWrappedLikeAnyOtherInvocation()
        {
            var direct = new CallGateChannel("Test.ThrowDirect") { Func = new Func<bool>(() => throw new InvalidOperationException("from the provider")) };
            var converted = new CallGateChannel("Test.ThrowConverted") { Func = new Func<int>(() => throw new InvalidOperationException("from the provider")) };
            var withArgument = new CallGateChannel("Test.ThrowArgument") { Func = new Func<int, bool>(_ => throw new InvalidOperationException("from the provider")) };

            foreach (var call in new Action[]
                     {
                         () => direct.InvokeFunc<bool>(NoArgs),
                         () => converted.InvokeFunc<long>(NoArgs),
                         () => withArgument.InvokeFunc<bool>([1]),
                     })
            {
                var thrown = Assert.Throws<TargetInvocationException>(call);
                Assert.Equal("from the provider", Assert.IsType<InvalidOperationException>(thrown.InnerException).Message);
            }
        }

        [Fact]
        public void TheArgumentCountIsStillChecked()
        {
            var noParameter = new CallGateChannel("Test.Length0") { Func = new Func<bool>(() => true) };
            var oneParameter = new CallGateChannel("Test.Length1") { Func = new Func<int, bool>(x => x > 0) };

            // A delegate closed over the first argument of a static method reports that argument as a parameter.
            var closed = (Func<bool>)Delegate.CreateDelegate(
                typeof(Func<bool>),
                "text",
                typeof(CallGateChannelInvokeFuncTests).GetMethod(nameof(IsNotEmpty), BindingFlags.Static | BindingFlags.NonPublic)!);
            Assert.True(closed());
            var closedChannel = new CallGateChannel("Test.Closed") { Func = closed };

            Assert.Throws<IpcLengthMismatchError>(() => noParameter.InvokeFunc<bool>([1]));
            Assert.Throws<IpcLengthMismatchError>(() => oneParameter.InvokeFunc<bool>(NoArgs));
            Assert.Throws<IpcLengthMismatchError>(() => oneParameter.InvokeFunc<bool>(null));
            Assert.True(oneParameter.InvokeFunc<bool>([3]));
            Assert.Throws<IpcLengthMismatchError>(() => closedChannel.InvokeFunc<bool>(NoArgs));
        }

        [Fact]
        public void EveryHandlerOfACombinedDelegateRunsAndTheLastOneAnswers()
        {
            var order = string.Empty;
            Func<int> first = () =>
            {
                order += "a";
                return 1;
            };
            Func<int> second = () =>
            {
                order += "b";
                return 2;
            };
            var channel = new CallGateChannel("Test.Combined") { Func = first + second };

            Assert.Equal(2, channel.InvokeFunc<int>(NoArgs));
            Assert.Equal("ab", order);
        }

        [Fact]
        public void AValueTypeResultIsNotBoxed()
        {
            var counter = 0;
            var boolChannel = new CallGateChannel("Test.AllocBool") { Func = new Func<bool>(() => (++counter & 1) == 0) };
            var longChannel = new CallGateChannel("Test.AllocLong") { Func = new Func<long>(() => ++counter) };

            long Measure(Action body)
            {
                for (var i = 0; i < 2000; i++)
                    body();

                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 1000; i++)
                    body();
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Assert.Equal(0, Measure(() => boolChannel.InvokeFunc<bool>(NoArgs)));
            Assert.Equal(0, Measure(() => longChannel.InvokeFunc<long>(NoArgs)));
        }

        private static string ReturnsPayloadAsString() => "payload";

        private static bool IsNotEmpty(string text) => text.Length > 0;

        private sealed class Payload
        {
            public int Number { get; set; }

            public string Text { get; set; } = string.Empty;
        }

        private sealed class PayloadMirror
        {
            public int Number { get; set; }

            public string Text { get; set; } = string.Empty;
        }
    }
}
