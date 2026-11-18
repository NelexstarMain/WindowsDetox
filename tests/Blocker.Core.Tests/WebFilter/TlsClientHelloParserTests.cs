using System.Text;
using Blocker.Core.WebFilter;
using static Blocker.Core.Tests.WebFilter.TlsTestData;

namespace Blocker.Core.Tests.WebFilter;

public sealed class TlsClientHelloParserTests
{
    private static readonly byte[] Padding = Extension(21, new byte[40]);

    [Fact]
    public async Task Extracts_the_sni_of_a_real_client_hello()
    {
        byte[] hello = await CaptureRealClientHelloAsync("www.example.org", TestContext.Current.CancellationToken);

        Assert.True(TlsClientHelloParser.TryGetSni(hello, out string? sni));
        Assert.Equal("www.example.org", sni, ignoreCase: true);
        Assert.Equal(ClientHelloStatus.Found, TlsClientHelloParser.Parse(hello, out _));
    }

    [Fact]
    public async Task Real_client_hello_prefixes_need_more_data_and_never_throw()
    {
        byte[] hello = await CaptureRealClientHelloAsync("www.example.org", TestContext.Current.CancellationToken);

        for (int length = 0; length < hello.Length; length++)
        {
            Assert.Equal(ClientHelloStatus.NeedMoreData, TlsClientHelloParser.Parse(hello.AsSpan(0, length), out string? sni));
            Assert.Null(sni);
            Assert.False(TlsClientHelloParser.TryGetSni(hello.AsSpan(0, length), out _));
        }
    }

    [Fact]
    public async Task Bytes_after_the_first_record_are_ignored()
    {
        byte[] hello = await CaptureRealClientHelloAsync("www.example.org", TestContext.Current.CancellationToken);

        Assert.True(TlsClientHelloParser.TryGetSni([.. hello, 0x17, 3, 3, 0, 1, 0], out string? sni));
        Assert.Equal("www.example.org", sni, ignoreCase: true);
    }

    [Fact]
    public async Task Bit_flips_in_a_real_client_hello_never_throw()
    {
        byte[] hello = await CaptureRealClientHelloAsync("www.example.org", TestContext.Current.CancellationToken);

        for (int i = 0; i < hello.Length; i++)
        {
            foreach (byte mask in (ReadOnlySpan<byte>)[0x01, 0x80, 0xff])
            {
                byte[] copy = (byte[])hello.Clone();
                copy[i] ^= mask;

                ClientHelloStatus status = TlsClientHelloParser.Parse(copy, out string? sni);

                Assert.Equal(status == ClientHelloStatus.Found, sni is not null);
            }
        }
    }

    [Fact]
    public void Random_garbage_never_throws()
    {
        var random = new Random(20261117);
        for (int i = 0; i < 5000; i++)
        {
            byte[] data = new byte[random.Next(0, 700)];
            random.NextBytes(data);
            if (data.Length > 5 && i % 2 == 0)
            {
                // Plausible record header so the walk goes deeper.
                (data[0], data[1], data[2], data[3], data[4]) = (0x16, 3, 1, (byte)(data.Length - 5 >> 8), (byte)(data.Length - 5));
                data[5] = 1;
            }

            ClientHelloStatus status = TlsClientHelloParser.Parse(data, out string? sni);

            Assert.Equal(status == ClientHelloStatus.Found, sni is not null);
        }
    }

    [Fact]
    public void Synthetic_hello_with_sni_among_other_extensions()
    {
        byte[] hello = ClientHello([Padding, ServerNameExtension("Shop.Example.com"), Padding], sessionIdLength: 32);

        Assert.Equal(ClientHelloStatus.Found, TlsClientHelloParser.Parse(hello, out string? sni));
        Assert.Equal("Shop.Example.com", sni);
    }

    [Fact]
    public void Truncated_synthetic_hello_needs_more_data()
    {
        byte[] hello = ClientHello([ServerNameExtension("example.com")]);

        for (int length = 0; length < hello.Length; length++)
        {
            Assert.Equal(ClientHelloStatus.NeedMoreData, TlsClientHelloParser.Parse(hello.AsSpan(0, length), out _));
        }
    }

    [Fact]
    public void Hello_without_extensions_or_without_server_name_has_no_sni()
    {
        Assert.Equal(ClientHelloStatus.NoSni, TlsClientHelloParser.Parse(ClientHello(null), out _));
        Assert.Equal(ClientHelloStatus.NoSni, TlsClientHelloParser.Parse(ClientHello([Padding]), out _));
        Assert.Equal(ClientHelloStatus.NoSni, TlsClientHelloParser.Parse(ClientHello([ServerNameExtension((1, [1, 2, 3]))]), out string? sni));
        Assert.Null(sni);
        Assert.False(TlsClientHelloParser.TryGetSni(ClientHello(null), out _));
    }

    [Fact]
    public void Smuggling_attempts_are_invalid()
    {
        Assert.Equal(ClientHelloStatus.Invalid, TlsClientHelloParser.Parse(ClientHello([ServerNameExtension("a.com"), ServerNameExtension("b.com")]), out string? sni));
        Assert.Null(sni);
        Assert.Equal(ClientHelloStatus.Invalid, TlsClientHelloParser.Parse(ClientHello([ServerNameExtension("a.com", "b.com")]), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("exa mple.com")]
    [InlineData("exaémple.com")]
    [InlineData("a/b.com")]
    public void Malformed_host_names_are_invalid(string name)
    {
        Assert.Equal(ClientHelloStatus.Invalid, TlsClientHelloParser.Parse(ClientHello([ServerNameExtension(name)]), out _));
    }

    [Fact]
    public void Overlong_host_name_is_invalid()
    {
        Assert.Equal(ClientHelloStatus.Invalid, TlsClientHelloParser.Parse(ClientHello([ServerNameExtension(new string('a', 256))]), out _));
    }

    [Fact]
    public void Structural_violations_are_invalid()
    {
        byte[] good = ClientHello([ServerNameExtension("example.com")]);

        Assert.Equal(ClientHelloStatus.Invalid, TlsClientHelloParser.Parse(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n"), out _));
        Assert.Equal(ClientHelloStatus.Invalid, TlsClientHelloParser.Parse(Record([1, 0, 0, 0], contentType: 0x17), out _));
        Assert.Equal(ClientHelloStatus.Invalid, TlsClientHelloParser.Parse(Record([2, 0, 0, 0]), out _));
        Assert.Equal(ClientHelloStatus.Invalid, TlsClientHelloParser.Parse([0x16, 2, 0, 0, 10, 1, 0, 0, 6], out _));
        Assert.Equal(ClientHelloStatus.Invalid, TlsClientHelloParser.Parse([0x16, 3, 1, 0x80, 0, 1], out _));
        Assert.Equal(ClientHelloStatus.Invalid, TlsClientHelloParser.Parse([0x16, 3, 1, 0, 2, 1, 0], out _));

        // Handshake length larger than the record (fragmented hello, unsupported).
        byte[] fragmented = (byte[])good.Clone();
        fragmented[8]++;
        Assert.Equal(ClientHelloStatus.Invalid, TlsClientHelloParser.Parse(fragmented, out _));

        // Extensions length disagreeing with the remaining bytes.
        byte[] badExtensions = (byte[])good.Clone();
        badExtensions[5 + 4 + 2 + 32 + 1 + 2 + 4 + 2 + 1] ^= 0x01;
        Assert.Equal(ClientHelloStatus.Invalid, TlsClientHelloParser.Parse(badExtensions, out _));
    }

    [Fact]
    public void Oversized_session_id_and_odd_cipher_suite_lengths_are_invalid()
    {
        Assert.Equal(ClientHelloStatus.Invalid, TlsClientHelloParser.Parse(ClientHello([ServerNameExtension("a.com")], sessionIdLength: 33), out _));

        byte[] hello = ClientHello([ServerNameExtension("a.com")]);
        hello[5 + 4 + 2 + 32 + 1 + 1] = 3;
        Assert.Equal(ClientHelloStatus.Invalid, TlsClientHelloParser.Parse(hello, out _));
    }

    [Fact]
    public void Empty_input_needs_more_data()
    {
        Assert.Equal(ClientHelloStatus.NeedMoreData, TlsClientHelloParser.Parse([], out _));
        Assert.Equal(ClientHelloStatus.NeedMoreData, TlsClientHelloParser.Parse([0x16], out _));
        Assert.Equal(ClientHelloStatus.Invalid, TlsClientHelloParser.Parse([0x47], out _));
    }
}
