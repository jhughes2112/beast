using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;


// Working time for one model call: the time spent sending the request and receiving the response, and not
// the time between them, when the request is only waiting its turn at the gateway. It is what a session's
// WorkedMs is made of, along with what its tools report. LlmService opens a meter around each call.
public sealed class WorkMeter
{
	private static readonly AsyncLocal<WorkMeter?> _current = new AsyncLocal<WorkMeter?>();

	private long _ticks;

	public static WorkMeter? Current
	{
		get => _current.Value;
		set => _current.Value = value;
	}

	public long Ms => Interlocked.Read(ref _ticks) * 1000 / Stopwatch.Frequency;

	public void Add(long stopwatchTicks)
	{
		Interlocked.Add(ref _ticks, stopwatchTicks);
	}

	// Data arrived. Counts the time since the last arrival, kept in mark, which starts at 0: the first arrival
	// only starts the clock, since the wait before it is not work.
	public void Received(ref long mark)
	{
		long now = Stopwatch.GetTimestamp();
		if (mark != 0)
			Add(now - mark);
		mark = now;
	}
}

// Working time for one tool call, which every call has, whatever the tool does. It runs from the start of
// the call, except while the tool waits on something else: a model, a child session or a person. What a
// sub-call worked is reported into it when the sub-call returns. ToolDispatch adds the total to the session.
public sealed class ToolClock
{
	private static readonly AsyncLocal<ToolClock?> _current = new AsyncLocal<ToolClock?>();

	private readonly object _lock    = new object();
	private readonly long   _started = Stopwatch.GetTimestamp();
	private          long   _waitedTicks;
	private          long   _waitStarted;
	private          int    _waiters;
	private          long   _subWorkMs;

	public static ToolClock? Current
	{
		get => _current.Value;
		set => _current.Value = value;
	}

	public long WorkedMs
	{
		get
		{
			lock (_lock)
			{
				long now    = Stopwatch.GetTimestamp();
				long waited = _waitedTicks + (_waiters > 0 ? now - _waitStarted : 0);
				return (now - _started - waited) * 1000 / Stopwatch.Frequency + _subWorkMs;
			}
		}
	}

	// Stops the current call's clock until the result is disposed. Waits can overlap and nest. Outside a tool
	// call it does nothing, so code shared with other paths can always say when it is waiting.
	public static Waiting Wait()
	{
		ToolClock? clock = Current;
		clock?.BeginWait();
		return new Waiting(clock);
	}

	// A sub-call's time. Outside a tool call, as when compacting, it belongs to the session directly.
	public static void Report(Session session, long workedMs)
	{
		ToolClock? clock = Current;
		if (clock == null)
		{
			session.RecordWork(workedMs);
		}
		else
		{
			lock (clock._lock)
				clock._subWorkMs += workedMs;
		}
	}

	private void BeginWait()
	{
		lock (_lock)
		{
			if (_waiters++ == 0)
				_waitStarted = Stopwatch.GetTimestamp();
		}
	}

	private void EndWait()
	{
		lock (_lock)
		{
			if (--_waiters == 0)
				_waitedTicks += Stopwatch.GetTimestamp() - _waitStarted;
		}
	}

	public readonly struct Waiting : IDisposable
	{
		private readonly ToolClock? _clock;

		public Waiting(ToolClock? clock)
		{
			_clock = clock;
		}

		public void Dispose()
		{
			_clock?.EndWait();
		}
	}
}

// Sits under the shared HttpClient and times what passes through while a meter is open. Other requests pass
// straight through.
public sealed class WorkTimingHandler : DelegatingHandler
{
	public WorkTimingHandler(HttpMessageHandler inner) : base(inner)
	{
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		WorkMeter? meter = WorkMeter.Current;
		if (meter != null && request.Content != null)
			request.Content = new TimedRequestContent(request.Content, meter);

		HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
		if (meter != null)
			response.Content = new TimedResponseContent(response.Content, meter);
		return response;
	}

	private static void CopyHeaders(HttpContent from, HttpContent to)
	{
		foreach (System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.IEnumerable<string>> header in from.Headers)
			to.Headers.TryAddWithoutValidation(header.Key, header.Value);
	}

	private sealed class TimedRequestContent : HttpContent
	{
		private readonly HttpContent _inner;
		private readonly WorkMeter   _meter;

		public TimedRequestContent(HttpContent inner, WorkMeter meter)
		{
			_inner = inner;
			_meter = meter;
			CopyHeaders(inner, this);
		}

		protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
		{
			long start = Stopwatch.GetTimestamp();
			await _inner.CopyToAsync(stream);
			_meter.Add(Stopwatch.GetTimestamp() - start);
		}

		protected override bool TryComputeLength(out long length)
		{
			long? known = _inner.Headers.ContentLength;
			length      = known ?? 0;
			return known.HasValue;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				_inner.Dispose();
			base.Dispose(disposing);
		}
	}

	private sealed class TimedResponseContent : HttpContent
	{
		private readonly HttpContent _inner;
		private readonly WorkMeter   _meter;

		public TimedResponseContent(HttpContent inner, WorkMeter meter)
		{
			_inner = inner;
			_meter = meter;
			CopyHeaders(inner, this);
		}

		protected override async Task<Stream> CreateContentReadStreamAsync()
		{
			return new TimedStream(await _inner.ReadAsStreamAsync(), _meter);
		}

		protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
		{
			using Stream timed = await CreateContentReadStreamAsync();
			await timed.CopyToAsync(stream);
		}

		protected override bool TryComputeLength(out long length)
		{
			long? known = _inner.Headers.ContentLength;
			length      = known ?? 0;
			return known.HasValue;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				_inner.Dispose();
			base.Dispose(disposing);
		}
	}

	private sealed class TimedStream : Stream
	{
		private readonly Stream    _inner;
		private readonly WorkMeter _meter;
		private          long      _mark;

		public TimedStream(Stream inner, WorkMeter meter)
		{
			_inner = inner;
			_meter = meter;
		}

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			int read = await _inner.ReadAsync(buffer, cancellationToken);
			Arrived(buffer.Span.Slice(0, read));
			return read;
		}

		public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		{
			int read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
			Arrived(buffer.AsSpan(offset, read));
			return read;
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			int read = _inner.Read(buffer, offset, count);
			Arrived(buffer.AsSpan(offset, read));
			return read;
		}

		// A gateway sends SSE comments to keep the connection open while a request waits or a model loads, so
		// those are not the response starting.
		private void Arrived(ReadOnlySpan<byte> data)
		{
			if (data.Length > 0 && (_mark != 0 || data[0] != (byte)':'))
				_meter.Received(ref _mark);
		}

		public override bool CanRead  => true;
		public override bool CanSeek  => false;
		public override bool CanWrite => false;
		public override long Length   => throw new NotSupportedException();
		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}
		public override void Flush    () { }
		public override long Seek     (long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value)                     => throw new NotSupportedException();
		public override void Write    (byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				_inner.Dispose();
			base.Dispose(disposing);
		}
	}
}