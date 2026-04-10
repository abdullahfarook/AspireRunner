// using System.Reactive.Disposables;
// using System.Reactive.Linq;
// using System.Reactive.Subjects;
// using static System.String;
//
// namespace ProcessManager.Core.ProcessRegistry;
//
// public sealed class ThrottledBurstLogger : IDisposable
// {
//     private readonly Subject<string> _outputSubject = new();
//     private readonly Subject<string> _errorSubject  = new();
//     private readonly CompositeDisposable _disposables;
//     public ThrottledBurstLogger(ManagedProcess.BurstLoggerSink? sink, int burstDelayMs = 500)
//     {
//         var outputSub = _outputSubject
//             .BufferUntilQuiet(TimeSpan.FromMilliseconds(burstDelayMs))
//             .Where(b => b.Count > 0)
//             .Subscribe(b => sink?.LogOutput(Join(Environment.NewLine, b)));
//
//         var errorSub = _errorSubject
//             .BufferUntilQuiet(TimeSpan.FromMilliseconds(burstDelayMs))
//             .Where(b => b.Count > 0)
//             .Subscribe(b => sink?.LogError(Join(Environment.NewLine, b)));
//
//         _disposables = new CompositeDisposable(outputSub, errorSub, _outputSubject, _errorSubject);
//     }
//
//     public void EnqueueOutput(string line) => _outputSubject.OnNext(line);
//     public void EnqueueError(string line)  => _errorSubject.OnNext(line);
//
//     public void Dispose() => _disposables.Dispose();
// }
// public static class ObservableExtensions
// {
//     public static IObservable<IList<T>> BufferUntilQuiet<T>(
//         this IObservable<T> source, TimeSpan quietPeriod, TimeSpan? maxWait = null)
//     {
//         var boundary = source
//             .Throttle(quietPeriod)
//             .Select(_ => Unit.Default);
//
//         if (maxWait.HasValue)
//             boundary = boundary
//                 .Merge(Observable.Interval(maxWait.Value)
//                     .Select(_ => Unit.Default));
//
//         return source
//             .Buffer(boundary)
//             .Where(b => b.Count > 0);
//     }
// }