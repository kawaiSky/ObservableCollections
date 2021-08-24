﻿using ObservableCollections.Internal;
using System.Collections;
using System.Collections.Specialized;

namespace ObservableCollections
{
    public sealed partial class ObservableRingBuffer<T>
    {
        public ISynchronizedView<T, TView> CreateView<TView>(Func<T, TView> transform, bool reverse = false)
        {
            return new View<TView>(this, transform, reverse);
        }

        sealed class View<TView> : ISynchronizedView<T, TView>
        {
            readonly ObservableRingBuffer<T> source;
            readonly Func<T, TView> selector;
            readonly bool reverse;
            readonly RingBuffer<(T, TView)> ringBuffer;

            ISynchronizedViewFilter<T, TView> filter;

            public event NotifyCollectionChangedEventHandler<T>? RoutingCollectionChanged;
            public event Action<NotifyCollectionChangedAction>? CollectionStateChanged;

            public object SyncRoot { get; }

            public View(ObservableRingBuffer<T> source, Func<T, TView> selector, bool reverse)
            {
                this.source = source;
                this.selector = selector;
                this.reverse = reverse;
                this.filter = SynchronizedViewFilter<T, TView>.Null;
                this.SyncRoot = new object();
                lock (source.SyncRoot)
                {
                    this.ringBuffer = new RingBuffer<(T, TView)>(source.buffer.Select(x => (x, selector(x))));
                    this.source.CollectionChanged += SourceCollectionChanged;
                }
            }

            public int Count
            {
                get
                {
                    lock (SyncRoot)
                    {
                        return ringBuffer.Count;
                    }
                }
            }

            public void AttachFilter(ISynchronizedViewFilter<T, TView> filter)
            {
                lock (SyncRoot)
                {
                    this.filter = filter;
                    foreach (var (value, view) in ringBuffer)
                    {
                        filter.InvokeOnAttach(value, view);
                    }
                }
            }

            public void ResetFilter(Action<T, TView>? resetAction)
            {
                lock (SyncRoot)
                {
                    this.filter = SynchronizedViewFilter<T, TView>.Null;
                    if (resetAction != null)
                    {
                        foreach (var (item, view) in ringBuffer)
                        {
                            resetAction(item, view);
                        }
                    }
                }
            }

            public INotifyCollectionChangedSynchronizedView<T, TView> WithINotifyCollectionChanged()
            {
                lock (SyncRoot)
                {
                    return new NotifyCollectionChangedSynchronizedView<T, TView>(this);
                }
            }

            public IEnumerator<(T, TView)> GetEnumerator()
            {
                if (!reverse)
                {
                    return new SynchronizedViewEnumerator<T, TView>(SyncRoot, ringBuffer.AsEnumerable().GetEnumerator(), filter);
                }
                else
                {
                    return new SynchronizedViewEnumerator<T, TView>(SyncRoot, ringBuffer.AsEnumerable().Reverse().GetEnumerator(), filter);
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public void Dispose()
            {
                this.source.CollectionChanged -= SourceCollectionChanged;
            }

            private void SourceCollectionChanged(in NotifyCollectionChangedEventArgs<T> e)
            {
                lock (SyncRoot)
                {
                    switch (e.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                            // can not distinguish AddFirst and AddLast when collection count is 0.
                            // So, in that case, use AddLast.
                            // The internal structure may be different from the parent, but the result is same.
                            // RangeOperation is only exists AddLastRange because we can not distinguish FirstRange or LastRange.
                            if (e.NewStartingIndex == 0 && ringBuffer.Count != 0)
                            {
                                // AddFirst
                                if (e.IsSingleItem)
                                {
                                    var v = (e.NewItem, selector(e.NewItem));
                                    ringBuffer.AddFirst(v);
                                    filter.InvokeOnAdd(v);
                                }
                                else
                                {
                                    foreach (var item in e.NewItems)
                                    {
                                        var v = (item, selector(item));
                                        ringBuffer.AddFirst(v);
                                        filter.InvokeOnAdd(v);
                                    }
                                }
                            }
                            else
                            {
                                // AddLast
                                if (e.IsSingleItem)
                                {
                                    var v = (e.NewItem, selector(e.NewItem));
                                    ringBuffer.AddLast(v);
                                    filter.InvokeOnAdd(v);
                                }
                                else
                                {
                                    foreach (var item in e.NewItems)
                                    {
                                        var v = (item, selector(item));
                                        ringBuffer.AddLast(v);
                                        filter.InvokeOnAdd(v);
                                    }
                                }
                            }
                            break;
                        case NotifyCollectionChangedAction.Remove:
                            if (e.IsSingleItem)
                            {
                                var v = ringBuffer[e.OldStartingIndex];
                                ringBuffer.RemoveAt(e.OldStartingIndex);
                                filter.InvokeOnRemove(v.Item1, v.Item2);
                            }
                            else
                            {
                                var len = e.OldStartingIndex + e.OldItems.Length;
                                for (int i = e.OldStartingIndex; i < len; i++)
                                {
                                    var v = ringBuffer[i];
                                    filter.InvokeOnRemove(v.Item1, v.Item2);
                                }

                                ringBuffer.RemoveRange(e.OldStartingIndex, e.OldItems.Length);
                            }
                            break;
                        case NotifyCollectionChangedAction.Replace:
                            // ObservableList does not support replace range
                            {
                                var v = (e.NewItem, selector(e.NewItem));

                                var oldItem = ringBuffer[e.NewStartingIndex];
                                ringBuffer[e.NewStartingIndex] = v;

                                filter.InvokeOnRemove(oldItem);
                                filter.InvokeOnAdd(v);
                                break;
                            }
                        case NotifyCollectionChangedAction.Move:
                            {
                                var removeItem = ringBuffer[e.OldStartingIndex];
                                ringBuffer.RemoveAt(e.OldStartingIndex);
                                ringBuffer.Insert(e.NewStartingIndex, removeItem);

                                filter.InvokeOnMove(removeItem);
                            }
                            break;
                        case NotifyCollectionChangedAction.Reset:
                            if (!filter.IsNullFilter())
                            {
                                foreach (var item in ringBuffer)
                                {
                                    filter.InvokeOnRemove(item);
                                }
                            }
                            ringBuffer.Clear();
                            break;
                        default:
                            break;
                    }

                    RoutingCollectionChanged?.Invoke(e);
                    CollectionStateChanged?.Invoke(e.Action);
                }
            }
        }
    }
}