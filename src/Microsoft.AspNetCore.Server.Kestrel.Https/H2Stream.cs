// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AspNetCore.Server.Kestrel.Https
{
    public class H2Stream : Stream
    {
        private readonly Stream _inner;
        private bool _firstRead = true;
        private byte[] _clientHelloBuffer = new byte[1024];
        private int _clientHelloOffset = 0;
        private int _clientHelloBytes = 0;

        public H2Stream(Stream inner)
        {
            _inner = inner;
        }

        public bool HasH2
        {
            get;
            private set;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length
        {
            get
            {
                throw new NotSupportedException();
            }
        }

        public override long Position
        {
            get
            {
                throw new NotSupportedException();
            }
            set
            {
                throw new NotSupportedException();
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // ValueTask uses .GetAwaiter().GetResult() if necessary
            // https://github.com/dotnet/corefx/blob/f9da3b4af08214764a51b2331f3595ffaf162abe/src/System.Threading.Tasks.Extensions/src/System/Threading/Tasks/ValueTask.cs#L156
            return ReadAsync(buffer, offset, count, CancellationToken.None).Result;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (!_firstRead)
            {
                if (_clientHelloBytes > 0)
                {
                    var copyBytes = Math.Min(count, _clientHelloBytes);
                    for (var i = 0; i < copyBytes; i++)
                    {
                        buffer[offset++] = _clientHelloBuffer[_clientHelloOffset++];
                        _clientHelloBytes--;
                    }
                    Console.WriteLine($"ReadAsync returning {copyBytes}");
                    return copyBytes;
                }
                else
                {
                    Console.WriteLine($"ReadAsync pass through");
                    return await _inner.ReadAsync(buffer, offset, count, cancellationToken);
                }
            }

            await ReadClientHello();

            for (var i = 0; i < count; i++)
            {
                buffer[offset++] = _clientHelloBuffer[_clientHelloOffset++];
                _clientHelloBytes--;
            }

            Console.WriteLine($"ReadAsync returning {count}");

            return count;
        }

        private async Task ReadClientHello()
        {
            var buffer = _clientHelloBuffer;

            _firstRead = false;

            const int headerSize = 9;

            var bytesRead = 0;

            // Read header
            while (_clientHelloBytes < headerSize &&
                   (bytesRead = await _inner.ReadAsync(buffer, _clientHelloBytes, headerSize - _clientHelloBytes)) > 0)
            {
                _clientHelloBytes += bytesRead;
            }

            if (bytesRead == 0)
            {
                return;
            }

            Console.WriteLine($"Read {_clientHelloBytes}");

            // Check for client hello
            if (buffer[0] != 22 || buffer[5] != 1)
            {
                return;
            }

            Console.WriteLine($"{buffer[0]}");
            Console.WriteLine($"{buffer[5]}");

            var messageLength = (buffer[6] << 16 | buffer[7] << 8 | buffer[8]);
            var messageRead = 0;
            Console.WriteLine($"messageRead: {messageRead}");

            Console.WriteLine($"{messageLength}");

            // Read entire message
            while (messageRead < messageLength &&
                   (bytesRead = await _inner.ReadAsync(buffer, headerSize + messageRead, messageLength - messageRead)) > 0)
            {
                messageRead += bytesRead;
                Console.WriteLine($"messageRead: {messageRead}");
            }

            _clientHelloBytes += messageRead;

            Console.WriteLine($"{_clientHelloBytes}");

            if (bytesRead == 0)
            {
                return;
            }

            var i = headerSize;

            // Skip version
            i += 2;

            // Skip random
            i += 32;

            // Skip session ID
            var sessionIdLength = buffer[i++];
            Console.WriteLine($"sessionIdLength: {sessionIdLength}");
            i += sessionIdLength;

            // Skip cipher suites
            var cipherSuitesLength = (buffer[i++] << 8 | buffer[i++]);
            Console.WriteLine($"cipherSuitesLength: {cipherSuitesLength}");
            i += cipherSuitesLength;

            // Skip compression methods
            var compressionMethodsLength = buffer[i++];
            Console.WriteLine($"compressionMethodsLength: {compressionMethodsLength}");
            i += compressionMethodsLength;

            if (i == _clientHelloBytes)
            {
                return;
            }

            Console.WriteLine("Looking for h2");

            // Find h2
            var extensionsLength = (buffer[i++] << 8 | buffer[i++]);
            var l = extensionsLength;
            while (l > 0)
            {
                var extensionType = (buffer[i++] << 8 | buffer[i++]);
                var extensionLength = (buffer[i++] << 8 | buffer[i++]);

                if (extensionType == 16 && extensionLength > 0)
                {
                    if (CheckH2(buffer, i))
                    {
                        Console.WriteLine("found h2");
                        HasH2 = true;
                        break;
                    }
                }

                i += extensionLength;
                l -= 2 + 2 + extensionLength;
            }

            Console.WriteLine("Read ClientHello");
        }

        private bool CheckH2(byte[] data, int i)
        {
            var payloadLength = (data[i++] << 8 | data[i++]);

            var l = payloadLength;
            while (l > 0)
            {
                var stringLength = data[i++];

                if (stringLength == 2 && data[i++] == 'h' && data[i++] == '2')
                {
                    return true;
                }

                l -= 1 + stringLength;
            }

            return false;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            return _inner.WriteAsync(buffer, offset, count, token);
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return _inner.FlushAsync(cancellationToken);
        }

#if NET46
        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            var task = ReadAsync(buffer, offset, count, default(CancellationToken), state);
            if (callback != null)
            {
                task.ContinueWith(t => callback.Invoke(t));
            }
            return task;
        }

        public override int EndRead(IAsyncResult asyncResult)
        {
            return ((Task<int>)asyncResult).GetAwaiter().GetResult();
        }

        private Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken, object state)
        {
            var tcs = new TaskCompletionSource<int>(state);
            var task = ReadAsync(buffer, offset, count, cancellationToken);
            task.ContinueWith((task2, state2) =>
            {
                var tcs2 = (TaskCompletionSource<int>)state2;
                if (task2.IsCanceled)
                {
                    tcs2.SetCanceled();
                }
                else if (task2.IsFaulted)
                {
                    tcs2.SetException(task2.Exception);
                }
                else
                {
                    tcs2.SetResult(task2.Result);
                }
            }, tcs, cancellationToken);
            return tcs.Task;
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            var task = WriteAsync(buffer, offset, count, default(CancellationToken), state);
            if (callback != null)
            {
                task.ContinueWith(t => callback.Invoke(t));
            }
            return task;
        }

        public override void EndWrite(IAsyncResult asyncResult)
        {
            ((Task<object>)asyncResult).GetAwaiter().GetResult();
        }

        private Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken, object state)
        {
            var tcs = new TaskCompletionSource<object>(state);
            var task = WriteAsync(buffer, offset, count, cancellationToken);
            task.ContinueWith((task2, state2) =>
            {
                var tcs2 = (TaskCompletionSource<object>)state2;
                if (task2.IsCanceled)
                {
                    tcs2.SetCanceled();
                }
                else if (task2.IsFaulted)
                {
                    tcs2.SetException(task2.Exception);
                }
                else
                {
                    tcs2.SetResult(null);
                }
            }, tcs, cancellationToken);
            return tcs.Task;
        }
#elif NETSTANDARD1_3
#else
#error target frameworks need to be updated.
#endif
    }
}
