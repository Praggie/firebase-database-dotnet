﻿namespace Firebase.Database.Offline
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
    using System.Threading;
    using System.Threading.Tasks;

    using Firebase.Database.Query;
    using Firebase.Database.Streaming;

    /// <summary>
    /// The real-time database which synchronizes online and offline data. 
    /// </summary>
    /// <typeparam name="T"> Type of entities. </typeparam>
    public partial class RealtimeDatabase<T> where T : class
    {
        private readonly ChildQuery childQuery;
        private readonly bool streamChanges;
        private readonly IDictionary<string, OfflineEntry> database;
        private readonly Subject<FirebaseEvent<T>> subject;

        private IDisposable subscription;

        /// <summary>
        /// Initializes a new instance of the <see cref="RealtimeDatabase{T}"/> class.
        /// </summary>
        /// <param name="childQuery"> The child query. </param>
        /// <param name="offlineDatabaseFactory"> The offline database factory. </param>
        /// <param name="filenameModifier"> Custom string which will get appended to the file name. </param> 
        /// <param name="streamChanges"> Specifies whether changes should be streamed from the server. </param> 
        public RealtimeDatabase(ChildQuery childQuery, Func<Type, string, IDictionary<string, OfflineEntry>> offlineDatabaseFactory, string filenameModifier, bool streamChanges)
        {
            this.childQuery = childQuery;
            this.streamChanges = streamChanges;
            this.subject = new Subject<FirebaseEvent<T>>();
            this.database = offlineDatabaseFactory(typeof(T), filenameModifier);
        }

        /// <summary>
        /// Event raised whenever an exception is thrown in the synchronization thread. Exception thrown in there are swallowed, so this event is the only way to get to them. 
        /// </summary>
        public event EventHandler<ExceptionEventArgs> SyncExceptionThrown;

        /// <summary>
        /// Overwrites existing object with given key.
        /// </summary>
        /// <param name="key"> The key. </param>
        /// <param name="obj"> The object to set. </param>
        /// <param name="priority"> The priority. Objects with higher priority will be synced first. Lower number indicates higher priority. </param>
        public void Put(string key, T obj, int priority = 1)
        {
            if (this.database.ContainsKey(key))
            {
                this.database[key].SyncOptions = SyncOptions.Push;
                this.SetAndRaise(key, this.database[key]);
            }
            else
            {
                this.SetAndRaise(key, new OfflineEntry(key, obj, priority));
            }
        }

        /// <summary>
        /// Adds a new entity to the database.
        /// </summary>
        /// <param name="obj"> The object to add.  </param>
        /// <param name="priority"> The priority. Objects with higher priority will be synced first. Lower number indicates higher priority. </param>
        /// <returns> The generated key for this object. </returns>
        public string Post(T obj, int priority = 1)
        {
            var key = FirebaseKeyGenerator.Next();

            this.SetAndRaise(key, new OfflineEntry(key, obj, priority));

            return key;
        }

        /// <summary>
        /// Deletes the entity with the given key.
        /// </summary>
        /// <param name="key"> The key. </param>
        /// <param name="priority"> The priority. Objects with higher priority will be synced first. Lower number indicates higher priority. </param> 
        public void Delete(string key, int priority = 1)
        {
            this.SetAndRaise(key, new OfflineEntry(key, null, priority));
        }

        /// <summary>
        /// Fetches an object with the given key and adds it to the database.
        /// </summary>
        /// <param name="key"> The key. </param>
        /// <param name="priority"> The priority. Objects with higher priority will be synced first. Lower number indicates higher priority. </param>
        public void Pull(string key, int priority = 1)
        {
            if (!this.database.ContainsKey(key))
            {
                this.database[key] = new OfflineEntry(key, null, priority, SyncOptions.Pull);
            }
            else
            {
                this.database[key].SyncOptions = SyncOptions.Pull;
            }
        }

        /// <summary> 
        /// Starts observing the real-time database. Events will be fired both when change is done locally and remotely.
        /// </summary> 
        /// <returns> Stream of <see cref="FirebaseEvent{T}"/>. </returns>
        public IObservable<FirebaseEvent<T>> AsObservable()
        {
            Task.Factory.StartNew(this.SynchronizeThread, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            var initialData = this.database
                .Where(kvp => !string.IsNullOrEmpty(kvp.Value.Data) && kvp.Value.Data != "null")
                .Select(kvp => new FirebaseEvent<T>(kvp.Key, kvp.Value.Deserialize<T>(), FirebaseEventType.InsertOrUpdate))
                .ToList()
                .ToObservable();

            return initialData.Concat(this.subject); 
        }

        private void SetAndRaise(string key, OfflineEntry obj)
        {
            this.database[key] = obj;
            this.subject.OnNext(new FirebaseEvent<T>(key, obj?.Deserialize<T>(), string.IsNullOrEmpty(obj?.Data) ? FirebaseEventType.Delete : FirebaseEventType.InsertOrUpdate));
        }

        private async void SynchronizeThread()
        {
            if (this.subscription != null)
            {
                return;
            }

            if (this.streamChanges)
            { 
                this.subscription = new FirebaseSubscription<T>(this.subject, this.childQuery.OrderByKey().StartAt(() => this.GetLatestKey()), new FirebaseCache<T>(new OfflineCacheAdapter<string, T>(this.database))).Run();
            }
            else
            {
                this.subscription = Observable.Empty<string>().Subscribe(); // just a dummy IDisposable
            }

            while (true)
            {
                try
                {
                    var validEntries = this.database.Where(e => e.Value != null);
                    await this.PullEntriesAsync(validEntries.Where(kvp => kvp.Value.SyncOptions == SyncOptions.Pull));
                    await this.PushEntriesAsync(validEntries.Where(kvp => kvp.Value.SyncOptions == SyncOptions.Push));
                }
                catch (Exception ex)
                {
                    this.SyncExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex));
                }

                await Task.Delay(1000);
            }
        }

        private string GetLatestKey()
        {
            return this.database.OrderBy(o => o.Key, StringComparer.Ordinal).LastOrDefault().Key;
        }

        private async Task PushEntriesAsync(IEnumerable<KeyValuePair<string, OfflineEntry>> pushEntries)
        {
            var groups = pushEntries.GroupBy(pair => pair.Value.Priority);

            foreach (var group in groups)
            {
                var tasks = group.Select(kvp => this.childQuery.Child(kvp.Key).PutAsync(kvp.Value.Deserialize<T>())).ToList();

                await Task.WhenAll(tasks);

                this.ResetSyncOptions(group.Select(s => s.Key));
            }
        }

        private async Task PullEntriesAsync(IEnumerable<KeyValuePair<string, OfflineEntry>> pullEntries)
        {
            var taskGroups = pullEntries.GroupBy(pair => pair.Value.Priority).OrderBy(g => g.Key);

            foreach (var group in taskGroups)
            {
                var tasks = group.Select(pair => new { Key = pair.Key, Task = this.childQuery.Child(pair.Key).OnceSingleAsync<T>(), Priority = pair.Value.Priority }).ToList();

                await Task.WhenAll(tasks.Select(t => t.Task));

                foreach (var task in tasks)
                {
                    this.SetAndRaise(task.Key, new OfflineEntry(task.Key, task.Task.Result, task.Priority, SyncOptions.None));
                }
            }
        }

        private void ResetSyncOptions(IEnumerable<string> entries)
        {
            foreach (var key in entries)
            {
                var item = this.database[key];
                item.SyncOptions = SyncOptions.None;
                this.database[key] = item;
            }
        }
    }
}
