using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;


// Thread-safe web response cache with per-entry TTL expiration.
// Prevents hammering external sites and getting rate-limited or banned.
public class WebCache
{
	private record CacheEntry(string Content, DateTime ExpiresAt);

	private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
	private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

	// Get a cached response if it exists and hasn't expired.
	public string? Get(string url)
	{
		if (_cache.TryGetValue(url, out var entry) && DateTime.UtcNow < entry.ExpiresAt)
		{
			return entry.Content;
		}

		// Remove expired entry if present.
		_cache.TryRemove(url, out _);
		return null;
	}

	// Store a response with a TTL (default 30 seconds).
	public void Set(string url, string content, TimeSpan? ttl)
	{
		_cache[url] = new CacheEntry(content, DateTime.UtcNow + (ttl ?? DefaultTtl));
	}

	// Get or fetch: returns cached content if available, otherwise fetches and caches.
	public async Task<string> GetOrFetchAsync(string url, Func<Task<string>> fetcher, TimeSpan? ttl)
	{
		var cached = Get(url);
		if (cached != null)
		{
			return cached;
		}

		string content = await fetcher();
		Set(url, content, ttl);
		return content;
	}

	// Clear all cached entries.
	public void Clear() => _cache.Clear();

	// Remove a specific URL from cache.
	public void Remove(string url) => _cache.TryRemove(url, out _);
}