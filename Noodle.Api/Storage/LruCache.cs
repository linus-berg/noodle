namespace Noodle.Api.Storage;

/// <summary>
///   A thread safe least-recently-used cache bounded by total size.
/// </summary>
public sealed class LruCache<TKey, TValue>(long capacity)
  where TKey : notnull {
  private readonly LinkedList<Entry> order_ = [];
  private readonly Dictionary<TKey, LinkedListNode<Entry>> entries_ = new();
  private readonly Lock lock_ = new();
  private long size_;

  public long Size {
    get {
      lock (lock_) {
        return size_;
      }
    }
  }

  public TValue? Get(TKey key) {
    lock (lock_) {
      if (!entries_.TryGetValue(key, out LinkedListNode<Entry>? node)) {
        return default;
      }

      order_.Remove(node);
      order_.AddFirst(node);
      return node.Value.Value;
    }
  }

  public void Set(TKey key, TValue value, long size) {
    if (size > capacity) {
      Remove(key);
      return;
    }

    lock (lock_) {
      if (entries_.Remove(key, out LinkedListNode<Entry>? existing)) {
        order_.Remove(existing);
        size_ -= existing.Value.Size;
      }

      LinkedListNode<Entry> node = order_.AddFirst(
        new Entry {
          Key = key,
          Value = value,
          Size = size
        }
      );
      entries_[key] = node;
      size_ += size;
      while (size_ > capacity && order_.Last != null) {
        LinkedListNode<Entry> oldest = order_.Last;
        order_.RemoveLast();
        entries_.Remove(oldest.Value.Key);
        size_ -= oldest.Value.Size;
      }
    }
  }

  public void Remove(TKey key) {
    lock (lock_) {
      if (entries_.Remove(key, out LinkedListNode<Entry>? node)) {
        order_.Remove(node);
        size_ -= node.Value.Size;
      }
    }
  }

  private sealed class Entry {
    public required TKey Key { get; init; }
    public required TValue Value { get; init; }
    public required long Size { get; init; }
  }
}