﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sparrow.Binary;
using Sparrow.Collections;
using Sparrow.Extensions;
using Sparrow.Threading;
using Sparrow.Utils;

namespace Sparrow.Logging
{
    public sealed class LoggingSource
    {
        private const string DateFormat = "yyyy-MM-dd";
        private static readonly int DateFormatLength = DateFormat.Length;

        [ThreadStatic]
        private static string _currentThreadId;

        public static bool UseUtcTime;
        public long MaxFileSizeInBytes = 1024 * 1024 * 128;

        internal static long LocalToUtcOffsetInTicks;

        static LoggingSource()
        {
            LocalToUtcOffsetInTicks = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).Ticks;
            ThreadLocalCleanup.ReleaseThreadLocalState += () => _currentThreadId = null;
        }

        private readonly ManualResetEventSlim _hasEntries = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _readyToCompress = new ManualResetEventSlim(false);
        private readonly CancellationTokenSource _tokenSource = new CancellationTokenSource();
        private readonly ThreadLocal<LocalThreadWriterState> _localState;
        private Thread _loggingThread;
        private Thread _compressLoggingThread;
        private int _generation;
        private readonly ConcurrentQueue<WeakReference<LocalThreadWriterState>> _newThreadStates =
            new ConcurrentQueue<WeakReference<LocalThreadWriterState>>();

        private bool _updateLocalTimeOffset;
        private string _path;
        private readonly string _name;
        private string _dateString;
        private readonly MultipleUseFlag _keepLogging = new MultipleUseFlag(true);
        private int _logNumber;
        private DateTime _today;
        public bool IsInfoEnabled;
        public bool IsOperationsEnabled;

        private Stream _additionalOutput;

        private Stream _pipeSink;
        private static readonly int TimeToWaitForLoggingToEndInMilliseconds = 5_000;

        public static readonly LoggingSource Instance = new LoggingSource(LogMode.None, Path.GetTempPath(), "Logging", TimeSpan.FromDays(3), long.MaxValue)
        {
            _updateLocalTimeOffset = true
        };
        public static readonly LoggingSource AuditLog = new LoggingSource(LogMode.None, Path.GetTempPath(), "Audit Log", TimeSpan.MaxValue, long.MaxValue);

        private static readonly byte[] _headerRow =
            Encodings.Utf8.GetBytes($"Time,\tThread,\tLevel,\tSource,\tLogger,\tMessage,\tException{Environment.NewLine}");

        public class WebSocketContext
        {
            public LoggingFilter Filter { get; } = new LoggingFilter();
        }

        private readonly ConcurrentDictionary<WebSocket, WebSocketContext> _listeners =
            new ConcurrentDictionary<WebSocket, WebSocketContext>();

        public LogMode LogMode { get; private set; }
        public TimeSpan RetentionTime { get; private set; }
        public long RetentionSize { get; private set; }
        public bool Compress => _compressLoggingThread != null;

        private LogMode _oldLogMode;

        public async Task Register(WebSocket source, WebSocketContext context, CancellationToken token)
        {
            await source.SendAsync(new ArraySegment<byte>(_headerRow), WebSocketMessageType.Text, true, token).ConfigureAwait(false);

            lock (this)
            {
                if (_listeners.IsEmpty)
                {
                    _oldLogMode = LogMode;
                    SetupLogMode(LogMode.Information, _path, RetentionTime, RetentionSize, Compress);
                }
                if (_listeners.TryAdd(source, context) == false)
                    throw new InvalidOperationException("Socket was already added?");
            }

            AssertLogging();

            var arraySegment = new ArraySegment<byte>(new byte[512]);
            var buffer = new StringBuilder();
            var charBuffer = new char[Encodings.Utf8.GetMaxCharCount(arraySegment.Count)];
            while (token.IsCancellationRequested == false)
            {
                buffer.Length = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await source.ReceiveAsync(arraySegment, token).ConfigureAwait(false);
                    if (result.CloseStatus != null)
                    {
                        return;
                    }
                    var chars = Encodings.Utf8.GetChars(arraySegment.Array, 0, result.Count, charBuffer, 0);
                    buffer.Append(charBuffer, 0, chars);
                } while (!result.EndOfMessage);

                var commandResult = context.Filter.ParseInput(buffer.ToString());
                var maxBytes = Encodings.Utf8.GetMaxByteCount(commandResult.Length);
                // We take the easy way of just allocating a large buffer rather than encoding
                // in a loop since large replies here are very rare.
                if (maxBytes > arraySegment.Count)
                    arraySegment = new ArraySegment<byte>(new byte[Bits.PowerOf2(maxBytes)]);

                var numberOfBytes = Encodings.Utf8.GetBytes(commandResult, 0,
                    commandResult.Length,
                    arraySegment.Array,
                    0);

                await source.SendAsync(new ArraySegment<byte>(arraySegment.Array, 0, numberOfBytes),
                    WebSocketMessageType.Text, true,
                    token).ConfigureAwait(false);
            }
        }

        private void AssertLogging()
        {
            var thread = _loggingThread;
            if (thread == null)
                throw new InvalidOperationException("There is no logging thread.");

            if (_keepLogging == false)
                throw new InvalidOperationException("Logging is turned off.");
        }

        public LoggingSource(LogMode logMode, string path, string name, TimeSpan retentionTime, long retentionSize, bool compress = false)
        {
            _path = path;
            _name = name;
            _localState = new ThreadLocal<LocalThreadWriterState>(GenerateThreadWriterState);

            SetupLogMode(logMode, path, retentionTime, retentionSize, compress);
        }

        public void SetupLogMode(LogMode logMode, string path, TimeSpan? retentionTime, long? retentionSize, bool compress)
        {
            SetupLogMode(logMode, path, retentionTime ?? TimeSpan.MaxValue, retentionSize ?? long.MaxValue, compress);
        }

        public void SetupLogMode(LogMode logMode, string path, TimeSpan retentionTime, long retentionSize, bool compress)
        {
            lock (this)
            {
                if (LogMode == logMode && path == _path && retentionTime == RetentionTime && compress == Compress)
                    return;
                LogMode = logMode;
                _path = path;
                RetentionTime = retentionTime;
                RetentionSize = retentionSize;

                IsInfoEnabled = (logMode & LogMode.Information) == LogMode.Information;
                IsOperationsEnabled = (logMode & LogMode.Operations) == LogMode.Operations;

                Directory.CreateDirectory(_path);
                var copyLoggingThread = _loggingThread;
                var copyCompressLoggingThread = _compressLoggingThread;
                if (copyLoggingThread == null)
                {
                    StartNewLoggingThreads(compress);
                }
                else if (copyLoggingThread.ManagedThreadId == Thread.CurrentThread.ManagedThreadId)
                {
                    // have to do this on a separate thread
                    Task.Run((Action)Restart);
                }
                else
                {
                    Restart();
                }
                void Restart()
                {
                    _keepLogging.Lower();
                    _hasEntries.Set();
                    _readyToCompress.Set();

                    copyLoggingThread.Join();
                    copyCompressLoggingThread?.Join();

                    StartNewLoggingThreads(compress);
                }
            }
        }

        private void StartNewLoggingThreads(bool compress)
        {
            if (IsInfoEnabled == false &&
                IsOperationsEnabled == false)
                return;

            _keepLogging.Raise();
            _loggingThread = new Thread(BackgroundLogger)
            {
                IsBackground = true,
                Name = _name + " Thread"
            };
            _loggingThread.Start();
            if (compress)
            {
                _compressLoggingThread = new Thread(BackgroundLoggerCompress)
                {
                    IsBackground = true,
                    Name = _name + "Log Compression Thread"
                };
                _compressLoggingThread.Start();
            }
            else
            {
                _compressLoggingThread = null;
            }
        }

        public void EndLogging()
        {
            _keepLogging.Lower();
            _hasEntries.Set();
            _readyToCompress.Set();
            _tokenSource.Cancel();
            _loggingThread.Join(TimeToWaitForLoggingToEndInMilliseconds);
            _compressLoggingThread.Join();
        }

        private FileStream GetNewStream(long maxFileSize)
        {
            string[] logFiles;
            string[] logGzFiles;
            try
            {
                logFiles = Directory.GetFiles(_path, $"{_dateString}.*.log");
                logGzFiles = Directory.GetFiles(_path, $"{_dateString}.*.log.gz");
            }
            catch (Exception)
            {
                // Something went wrong we will try again later
                return null;
            }
            Array.Sort(logFiles);
            Array.Sort(logGzFiles);

            if (DateTime.Today != _today)
            {
                lock (this)
                {
                    if (DateTime.Today != _today)
                    {
                        _today = DateTime.Today;
                        _dateString = DateTime.Today.ToString(DateFormat, CultureInfo.InvariantCulture);
                        _logNumber = Math.Max(NextLogNumberForExtension(logFiles, "log"), NextLogNumberForExtension(logGzFiles, "log.gz"));
                    }
                }
            }

            UpdateLocalDateTimeOffset();

            while (true)
            {
                var nextLogNumber = Interlocked.Increment(ref _logNumber);
                var fileName = Path.Combine(_path, _dateString) + "." +
                               nextLogNumber.ToString("000", CultureInfo.InvariantCulture) + ".log";
                if (File.Exists(fileName) && new FileInfo(fileName).Length >= maxFileSize)
                    continue;

                LimitLogSize(logFiles, logGzFiles);
                var fileStream = SafeFileStream.Create(fileName, FileMode.Append, FileAccess.Write, FileShare.Read, 32 * 1024, false);
                fileStream.Write(_headerRow, 0, _headerRow.Length);
                return fileStream;
            }
        }

        private void LimitLogSize(string[] logFiles, string[] logGzFiles)
        {
            var logFilesInfo = logFiles.Select(f => new FileInfo(f));
            var logGzFilesInfo = logGzFiles.Select(f => new FileInfo(f));
            var totalLogSize = logFilesInfo.Sum(i => i.Length) + logGzFilesInfo.Sum(i => i.Length);

            foreach (var log in logGzFilesInfo.Reverse())
            {
                if (totalLogSize > RetentionSize)
                {
                    try
                    {
                        log.Delete();
                    }
                    catch (Exception)
                    {
                        // Something went wrong we will try again later
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            foreach (var log in logFilesInfo.Reverse())
            {
                if (totalLogSize > RetentionSize)
                {
                    try
                    {
                        log.Delete();
                    }
                    catch (Exception)
                    {
                        // Something went wrong we will try again later
                        return;
                    }
                }
                else
                {
                    return;
                }
            }
        }

        private void UpdateLocalDateTimeOffset()
        {
            if (_updateLocalTimeOffset == false || UseUtcTime)
                return;

            var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).Ticks;
            if (offset != LocalToUtcOffsetInTicks)
                Interlocked.Exchange(ref LocalToUtcOffsetInTicks, offset);
        }

        private int NextLogNumberForExtension(string[] files, string extension)
        {
            var lastLogFile = files.LastOrDefault();
            if (lastLogFile == null)
                return 0;

            int start = lastLogFile.LastIndexOf('.', lastLogFile.Length - "000.".Length - extension.Length);
            if (start == -1)
                return 0;

            try
            {
                start++;
                var length = lastLogFile.Length - ".".Length - extension.Length - start;
                var logNumber = lastLogFile.Substring(start, length);
                if (int.TryParse(logNumber, out var number) == false ||
                    number <= 0)
                    return 0;

                return --number;
            }
            catch
            {
                return 0;
            }
        }

        private void CleanupOldLogFiles()
        {
            string[] logFiles;
            try
            {
                logFiles = Directory.GetFiles(_path, "*.log.gz");
            }
            catch (Exception)
            {
                // Something went wrong we will try again later
                return;
            }

            foreach (var logFile in logFiles)
            {
                var inputSpan = logFile.Substring(0, DateFormatLength);
                if (DateTime.TryParse(inputSpan, out var logDateTime) == false)
                    continue;

                if (_today - logDateTime < RetentionTime)
                    continue;

                try
                {
                    File.Delete(logFile);
                }
                catch (Exception)
                {
                    // we don't actually care if we can't handle this scenario, we'll just try again later
                    // maybe something is currently reading the file?
                }
            }
        }

        private void CleanupAlreadyCompressedLogFiles(string lastLogFile)
        {
            string[] logFiles;
            try
            {
                logFiles = Directory.GetFiles(_path, "*.log");
            }
            catch (Exception)
            {
                // Something went wrong we will try again later
                return;
            }

            foreach (var logFile in logFiles)
            {
                try
                {
                    if (string.Compare(logFile, lastLogFile, StringComparison.Ordinal) < 0)
                    {
                        File.Delete(logFile);
                    }
                }
                catch (Exception)
                {
                    // we don't actually care if we can't handle this scenario, we'll just try again later
                    // maybe something is currently reading the file?
                }
            }
        }

        private LocalThreadWriterState GenerateThreadWriterState()
        {
            var currentThread = Thread.CurrentThread;
            var state = new LocalThreadWriterState
            {
                OwnerThread = currentThread.Name,
                ThreadId = currentThread.ManagedThreadId,
                Generation = _generation
            };
            _newThreadStates.Enqueue(new WeakReference<LocalThreadWriterState>(state));
            return state;
        }

        public void Log(ref LogEntry entry, TaskCompletionSource<object> tcs = null)
        {
            var state = _localState.Value;
            if (state.Generation != _generation)
            {
                state = _localState.Value = GenerateThreadWriterState();
            }

            if (state.Free.Dequeue(out var item))
            {
                item.Data.SetLength(0);
                item.WebSocketsList.Clear();
                item.Task = tcs;
                state.ForwardingStream.Destination = item.Data;
            }
            else
            {
                item = new WebSocketMessageEntry();
                item.Task = tcs;
                state.ForwardingStream.Destination = new MemoryStream();
            }

            foreach (var kvp in _listeners)
            {
                if (kvp.Value.Filter.Forward(ref entry))
                {
                    item.WebSocketsList.Add(kvp.Key);
                }
            }

            WriteEntryToWriter(state.Writer, ref entry);
            item.Data = state.ForwardingStream.Destination;

            state.Full.Enqueue(item, timeout: 128);

            _hasEntries.Set();
        }

        private void WriteEntryToWriter(StreamWriter writer, ref LogEntry entry)
        {
            if (_currentThreadId == null)
            {
                _currentThreadId = ", " + Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture) +
                                   ", ";
            }

            writer.Write(entry.At.GetDefaultRavenFormat(isUtc: LoggingSource.UseUtcTime));
            writer.Write(_currentThreadId);

            switch (entry.Type)
            {
                case LogMode.Information:
                    writer.Write("Information");
                    break;
                case LogMode.Operations:
                    writer.Write("Operations");
                    break;
            }

            writer.Write(", ");
            writer.Write(entry.Source);
            writer.Write(", ");
            writer.Write(entry.Logger);
            writer.Write(", ");
            writer.Write(entry.Message);

            if (entry.Exception != null)
            {
                writer.Write(", EXCEPTION: ");
                writer.Write(entry.Exception);
            }
            writer.WriteLine();
            writer.Flush();
        }

        public Logger GetLogger<T>(string source)
        {
            return GetLogger(source, typeof(T).FullName);
        }

        public Logger GetLogger(string source, string logger)
        {
            return new Logger(this, source, logger);
        }

        private void BackgroundLogger()
        {
            NativeMemory.EnsureRegistered();
            try
            {
                Interlocked.Increment(ref _generation);
                var threadStates = new List<WeakReference<LocalThreadWriterState>>();
                var threadStatesToRemove = new FastStack<WeakReference<LocalThreadWriterState>>();
                while (_keepLogging)
                {
                    try
                    {
                        var maxFileSize = MaxFileSizeInBytes;
                        using (var currentFile = GetNewStream(maxFileSize))
                        {
                            if (currentFile == null)
                            {
                                if (_keepLogging == false)
                                    return;
                                _hasEntries.Wait();
                                continue;
                            }

                            var sizeWritten = 0;

                            var foundEntry = true;

                            while (sizeWritten < maxFileSize)
                            {
                                if (foundEntry == false)
                                {
                                    if (_keepLogging == false)
                                        return;
                                    // we don't want to have fsync here, we just
                                    // want to send it to the OS
                                    currentFile.Flush(flushToDisk: false);
                                    if (_hasEntries.IsSet == false)
                                    {
                                        // about to go to sleep, so can check if need to update offset or create new file for today logs
                                        UpdateLocalDateTimeOffset();

                                        if (DateTime.Today != _today)
                                        {
                                            // let's create new file so its name will have today date
                                            break;
                                        }
                                    }

                                    _hasEntries.Wait();
                                    if (_keepLogging == false)
                                        return;

                                    _hasEntries.Reset();
                                }

                                foundEntry = false;
                                foreach (var threadStateRef in threadStates)
                                {
                                    if (threadStateRef.TryGetTarget(out LocalThreadWriterState threadState) == false)
                                    {
                                        threadStatesToRemove.Push(threadStateRef);
                                        continue;
                                    }

                                    for (var i = 0; i < 16; i++)
                                    {
                                        if (threadState.Full.Dequeue(out WebSocketMessageEntry item) == false)
                                            break;

                                        foundEntry = true;

                                        sizeWritten += ActualWriteToLogTargets(item, currentFile);

                                        threadState.Free.Enqueue(item);
                                    }
                                }

                                while (threadStatesToRemove.TryPop(out var ts))
                                    threadStates.Remove(ts);

                                if (_newThreadStates.IsEmpty)
                                    continue;

                                while (_newThreadStates.TryDequeue(out WeakReference<LocalThreadWriterState> result))
                                    threadStates.Add(result);

                                _hasEntries.Set(); // we need to start writing logs again from new thread states
                            }
                            _readyToCompress.Set();
                        }
                    }
                    catch (OutOfMemoryException)
                    {
                        Console.Error.WriteLine("ERROR! Out of memory exception while trying to log, will avoid logging for the next 5 seconds");

                        var time = 5000;
                        var current = Stopwatch.GetTimestamp();

                        while (time > 0 &&
                            _hasEntries.Wait(time))
                        {
                            _hasEntries.Reset();
                            time = (int)(((Stopwatch.GetTimestamp() - current) * Stopwatch.Frequency) / 1000);
                            foreach (var threadStateRef in threadStates)
                            {
                                DiscardThreadLogState(threadStateRef);
                            }
                            foreach (var newThreadState in _newThreadStates)
                            {
                                DiscardThreadLogState(newThreadState);
                            }
                            current = Stopwatch.GetTimestamp();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                var msg = $"FATAL ERROR trying to log!{Environment.NewLine}{e}";
                Console.Error.WriteLine(msg);
            }
            finally
            {
                _readyToCompress.Set();
            }
        }

        private void BackgroundLoggerCompress()
        {
            var lastZipped = "";
            while (true)
            {
                try
                {
                    if (_keepLogging == false)
                        //To eliminate blocking of shutdown
                        return;

                    _readyToCompress.Wait(_tokenSource.Token);
                    _readyToCompress.Reset();

                    string[] logFiles;
                    try
                    {
                        logFiles = Directory.GetFiles(_path, "*.log");
                    }
                    catch (Exception)
                    {
                        // Something went wrong we will try again later
                        
                        continue;
                    }

                    if (logFiles.Length <= 1)
                        //There is just the current log file
                        continue;

                    Array.Sort(logFiles);
                    var lastZippedIndex = Math.Abs(Array.BinarySearch(logFiles, lastZipped));

                    for (var i = lastZippedIndex; i < logFiles.Length - 1; i++)
                    {
                        try
                        {
                            using (var logStream = SafeFileStream.Create(logFiles[i], FileMode.Open, FileAccess.Read))
                            {
                                var newZippedFile = Path.Combine(_path, Path.GetFileNameWithoutExtension(logFiles[i]) + ".log.gz");
                                //If there is compressed file with the same name (probably due to a failure) it will be overwritten
                                using (var newFileStream = SafeFileStream.Create(newZippedFile, FileMode.Create, FileAccess.Write))
                                using (var compressionStream = new GZipStream(newFileStream, CompressionMode.Compress))
                                {
                                    logStream.CopyTo(compressionStream);
                                }
                            }
                        }
                        catch (Exception)
                        {
                            //Something went wrong we will try later again
                            break;
                        }

                        try
                        {
                            File.Delete(logFiles[i]);
                        }
                        catch (Exception)
                        {
                            // we don't actually care if we can't handle this scenario, we'll just try again later
                            // maybe something is currently reading the file?
                        }
                    }

                    CleanupAlreadyCompressedLogFiles(lastZipped);
                    CleanupOldLogFiles();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private static void DiscardThreadLogState(WeakReference<LocalThreadWriterState> threadStateRef)
        {
            if (threadStateRef.TryGetTarget(out LocalThreadWriterState threadState) == false)
                return;
            while (threadState.Full.Dequeue(out WebSocketMessageEntry _))
                break;
        }

        public void AttachPipeSink(Stream stream)
        {
            _pipeSink = stream;
        }

        public void DetachPipeSink()
        {
            _pipeSink = null;
        }

        private int ActualWriteToLogTargets(WebSocketMessageEntry item, Stream file)
        {
            item.Data.TryGetBuffer(out var bytes);
            file.Write(bytes.Array, bytes.Offset, bytes.Count);
            _additionalOutput?.Write(bytes.Array, bytes.Offset, bytes.Count);

            if (item.Task != null)
            {
                try
                {
                    file.Flush();
                    _additionalOutput?.Flush();
                }
                finally
                {
                    item.Task.TrySetResult(null);
                }
            }

            try
            {
                _pipeSink?.Write(bytes.Array, bytes.Offset, bytes.Count);
            }
            catch
            {
                // broken pipe
            }

            if (!_listeners.IsEmpty)
            {
                // this is rare
                SendToWebSockets(item, bytes);
            }

            item.Data.SetLength(0);
            item.WebSocketsList.Clear();

            return bytes.Count;
        }

        private Task[] _tasks = new Task[0];

        private void SendToWebSockets(WebSocketMessageEntry item, ArraySegment<byte> bytes)
        {
            if (_tasks.Length != item.WebSocketsList.Count)
                Array.Resize(ref _tasks, item.WebSocketsList.Count);

            for (int i = 0; i < item.WebSocketsList.Count; i++)
            {
                var socket = item.WebSocketsList[i];
                try
                {
                    _tasks[i] = socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception)
                {
                    RemoveWebSocket(socket);
                }
            }

            bool success;
            try
            {
                success = Task.WaitAll(_tasks, 250);
            }
            catch (Exception)
            {
                success = false;
            }

            if (success == false)
            {
                for (int i = 0; i < _tasks.Length; i++)
                {
                    if (_tasks[i].IsFaulted || _tasks[i].IsCanceled ||
                        _tasks[i].IsCompleted == false)
                    {
                        // this either timed out or errored, removing it.
                        RemoveWebSocket(item.WebSocketsList[i]);
                    }
                }
            }
        }

        private void RemoveWebSocket(WebSocket socket)
        {
            WebSocketContext value;
            _listeners.TryRemove(socket, out value);
            if (!_listeners.IsEmpty)
                return;

            lock (this)
            {
                if (_listeners.IsEmpty)
                {
                    SetupLogMode(_oldLogMode, _path, RetentionTime, RetentionSize, Compress);
                }
            }
        }

        public void EnableConsoleLogging()
        {
            _additionalOutput = Console.OpenStandardOutput();
        }

        public void DisableConsoleLogging()
        {
            using (_additionalOutput)
            {
                _additionalOutput = null;
            }
        }

        private class LocalThreadWriterState
        {
            public int Generation;

            public readonly ForwardingStream ForwardingStream;

            public readonly SingleProducerSingleConsumerCircularQueue<WebSocketMessageEntry> Free =
                new SingleProducerSingleConsumerCircularQueue<WebSocketMessageEntry>(1024);

            public readonly SingleProducerSingleConsumerCircularQueue<WebSocketMessageEntry> Full =
                new SingleProducerSingleConsumerCircularQueue<WebSocketMessageEntry>(1024);

            public readonly StreamWriter Writer;

            public LocalThreadWriterState()
            {
                ForwardingStream = new ForwardingStream();
                Writer = new StreamWriter(ForwardingStream);
            }

#pragma warning disable 414
            public string OwnerThread;
            public int ThreadId;
#pragma warning restore 414
        }


        private class ForwardingStream : Stream
        {
            public MemoryStream Destination;
            public override bool CanRead { get; } = false;
            public override bool CanSeek { get; } = false;
            public override bool CanWrite { get; } = true;

            public override long Length
            {
                get { throw new NotSupportedException(); }
            }

            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override void Flush()
            {
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Destination.Write(buffer, offset, count);
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
