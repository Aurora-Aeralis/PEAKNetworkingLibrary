using System;
using System.Collections;
using System.Collections.Generic;
using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class MessageListDeserializationFallbackTests
{
    private sealed class FixedSizeIntList : IList<int>, IList
    {
        private readonly int[] values = Array.Empty<int>();

        public int this[int index] { get => values[index]; set => throw new NotSupportedException(); }
        object? IList.this[int index] { get => values[index]; set => throw new NotSupportedException(); }

        public int Count => values.Length;
        public bool IsReadOnly => true;

        public bool IsFixedSize => true;
        public bool IsReadOnlyCollection => true;
        bool IList.IsReadOnly => true;
        bool IList.IsFixedSize => true;
        public bool IsSynchronized => false;
        public object SyncRoot { get; } = new();

        public void Add(int item) => throw new NotSupportedException();
        int IList.Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(int item) => Array.IndexOf(values, item) >= 0;
        bool IList.Contains(object? value) => value is int i && Contains(i);
        public void CopyTo(int[] array, int arrayIndex) => values.CopyTo(array, arrayIndex);
        void ICollection.CopyTo(Array array, int index) => ((ICollection)values).CopyTo(array, index);
        public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => values.GetEnumerator();
        public int IndexOf(int item) => Array.IndexOf(values, item);
        int IList.IndexOf(object? value) => value is int i ? IndexOf(i) : -1;
        public void Insert(int index, int item) => throw new NotSupportedException();
        void IList.Insert(int index, object? value) => throw new NotSupportedException();
        public bool Remove(int item) => throw new NotSupportedException();
        void IList.Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
    }

    [Fact]
    public void ReadObject_ListLikeFixedSizeTarget_FallsBackToTemporaryList()
    {
        var write = new Message(1u, "method", 0);
        write.WriteObject(typeof(List<int>), new List<int> { 4, 5, 6 });

        var read = new Message(write.ToArray());
        var result = read.ReadObject(typeof(FixedSizeIntList));

        var fallback = Assert.IsType<List<int>>(result);
        Assert.Equal(new[] { 4, 5, 6 }, fallback);
    }
}
