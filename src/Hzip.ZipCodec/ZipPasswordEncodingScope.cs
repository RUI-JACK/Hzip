// Hzip compatibility extension to SharpZipLib 1.4.2 (MIT).
// Password encodings are scoped to an async operation, never process-wide.
using System;
using System.Text;
using System.Threading;
namespace ICSharpCode.SharpZipLib.Zip
{
    public sealed class ZipPasswordEncodingScope : IDisposable
    {
        private static readonly AsyncLocal<Encoding> Current = new AsyncLocal<Encoding>();
        private readonly Encoding previous;
        public static byte[] Encode(string password) => (Current.Value ?? Encoding.UTF8).GetBytes(password);
        public ZipPasswordEncodingScope(Encoding encoding) { previous = Current.Value; Current.Value = encoding; }
        public void Dispose() { Current.Value = previous; }
    }
}
