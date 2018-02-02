﻿// Copyright (c) Alexander Bogarsukov.
// Licensed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace UnityFx.Async
{
	/// <summary>
	/// Implementation of <see cref="IAsyncOperation"/>.
	/// </summary>
	/// <seealso href="https://blogs.msdn.microsoft.com/nikos/2011/03/14/how-to-implement-the-iasyncresult-design-pattern/"/>
	/// <seealso cref="IAsyncResult"/>
	[DebuggerDisplay("{DebuggerDisplay,nq}")]
	public class AsyncResult : IAsyncOperation, IAsyncOperationCompletionSource, IEnumerator
	{
		#region data

		private const int _flagCompleted = 0x00100000;
		private const int _flagSynchronous = 0x00200000;
		private const int _flagCompletedSynchronously = _flagCompleted | _flagSynchronous;
		private const int _flagDisposed = 0x00400000;
		private const int _flagDoNotDispose = 0x10000000;
		private const int _statusMask = 0x0000000f;

		private readonly object _asyncState;

		private static IAsyncOperation _completedOperation;
		private static object _continuationCompletionSentinel = new object();

		private EventWaitHandle _waitHandle;
		private Exception _exception;

		private volatile object _continuation;
		private volatile int _flags;

		#endregion

		#region interface

		/// <summary>
		/// Returns <see langword="true"/> if the operation is disposed; <see langword="false"/> otherwise. Read only.
		/// </summary>
		protected bool IsDisposed => (_flags & _flagDisposed) != 0;

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncResult"/> class.
		/// </summary>
		public AsyncResult()
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncResult"/> class.
		/// </summary>
		/// <param name="asyncCallback">User-defined completion callback.</param>
		/// <param name="asyncState">User-defined data returned by <see cref="AsyncState"/>.</param>
		public AsyncResult(AsyncCallback asyncCallback, object asyncState)
		{
			_asyncState = asyncState;
			_continuation = asyncCallback;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncResult"/> class.
		/// </summary>
		/// <param name="setRunning">If set to <see langword="true"/> transitions the operation to <see cref="AsyncOperationStatus.Running"/> status; otherwise the status is set to <see cref="AsyncOperationStatus.Scheduled"/>.</param>
		public AsyncResult(bool setRunning)
		{
			_flags = setRunning ? StatusRunning : StatusScheduled;
		}

		/// <summary>
		/// Transitions the operation to <see cref="AsyncOperationStatus.Scheduled"/> state.
		/// </summary>
		/// <exception cref="InvalidOperationException">Thrown if the transition fails.</exception>
		/// <seealso cref="SetRunning"/>
		public void SetScheduled()
		{
			if (!TrySetStatusInternal(StatusScheduled))
			{
				throw new InvalidOperationException();
			}
		}

		/// <summary>
		/// Transitions the operation to <see cref="AsyncOperationStatus.Running"/> state.
		/// </summary>
		/// <exception cref="InvalidOperationException">Thrown if the transition fails.</exception>
		/// <seealso cref="SetScheduled"/>
		public void SetRunning()
		{
			if (!TrySetStatusInternal(StatusRunning))
			{
				throw new InvalidOperationException();
			}
		}

		/// <summary>
		/// Throws <see cref="ObjectDisposedException"/> if this operation has been disposed.
		/// </summary>
		protected void ThrowIfDisposed()
		{
			if ((_flags & _flagDisposed) != 0)
			{
				throw new ObjectDisposedException(ToString());
			}
		}

		#endregion

		#region virtual interface

		/// <summary>
		/// Called when the operation state has changed.
		/// </summary>
		protected virtual void OnStatusChanged()
		{
		}

		/// <summary>
		/// Called when the operation is completed.
		/// </summary>
		protected virtual void OnCompleted()
		{
			_waitHandle?.Set();
			InvokeContinuation();
		}

		/// <summary>
		/// Releases unmanaged resources used by the object.
		/// </summary>
		/// <param name="disposing">Should be <see langword="true"/> if the method is called from <see cref="Dispose()"/>; <see langword="false"/> otherwise.</param>
		/// <seealso cref="Dispose()"/>
		/// <seealso cref="ThrowIfDisposed"/>
		protected virtual void Dispose(bool disposing)
		{
			if (disposing && (_flags & _flagDoNotDispose) == 0)
			{
				_flags |= _flagDisposed;

				if (_waitHandle != null)
				{
					_waitHandle.Close();
					_waitHandle = null;
				}
			}
		}

		#endregion

		#region static interface

		/// <summary>
		/// Returns an operation that's already been completed successfully.
		/// </summary>
		/// <remarks>
		/// May not always return the same instance.
		/// </remarks>
		public static IAsyncOperation Completed
		{
			get
			{
				if (_completedOperation == null)
				{
					_completedOperation = new AsyncResult(_flagDoNotDispose | _flagCompletedSynchronously | StatusRanToCompletion);
				}

				return _completedOperation;
			}
		}

		/// <summary>
		/// Creates a <see cref="IAsyncOperation"/> that has completed with a specified exception.
		/// </summary>
		/// <param name="e">The exception with which to complete the operation.</param>
		/// <returns>The faulted operation.</returns>
		public static IAsyncOperation FromException(Exception e)
		{
			var op = new AsyncResult();
			op.SetException(e, true);
			return op;
		}

		/// <summary>
		/// Creates a <see cref="IAsyncOperation{T}"/> that has completed with a specified exception.
		/// </summary>
		/// <param name="e">The exception with which to complete the operation.</param>
		/// <returns>The faulted operation.</returns>
		public static IAsyncOperation<T> FromException<T>(Exception e)
		{
			var op = new AsyncResult<T>();
			op.SetException(e, true);
			return op;
		}

		/// <summary>
		/// Creates a <see cref="IAsyncOperation{T}"/> that has completed with a specified result.
		/// </summary>
		/// <param name="result">The result value with which to complete the operation.</param>
		/// <returns>The completed operation.</returns>
		public static IAsyncOperation<T> FromResult<T>(T result)
		{
			var op = new AsyncResult<T>();
			op.SetResult(result, true);
			return op;
		}

		/// <summary>
		/// Creates an operation that completes after a time delay.
		/// </summary>
		/// <param name="millisecondsDelay">The number of milliseconds to wait before completing the returned operation, or <see cref="Timeout.Infinite"/> (-1) to wait indefinitely.</param>
		/// <exception cref="ArgumentOutOfRangeException">Thrown if the <paramref name="millisecondsDelay"/> is less than -1.</exception>
		/// <returns>An operation that represents the time delay.</returns>
		public static IAsyncOperation Delay(int millisecondsDelay)
		{
			if (millisecondsDelay < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(millisecondsDelay));
			}

			if (millisecondsDelay == 0)
			{
				return Completed;
			}

			if (millisecondsDelay == Timeout.Infinite)
			{
				return new AsyncResult();
			}

			return new DelayAsyncResult(millisecondsDelay);
		}

		/// <summary>
		/// Creates a task that completes after a specified time interval.
		/// </summary>
		/// <param name="delay">The time span to wait before completing the returned task, or <c>TimeSpan.FromMilliseconds(-1)</c> to wait indefinitely.</param>
		/// <exception cref="ArgumentOutOfRangeException">Thrown if the <paramref name="delay"/> represents a negative time interval other than <c>TimeSpan.FromMillseconds(-1)</c>.</exception>
		/// <returns>An operation that represents the time delay.</returns>
		public static IAsyncOperation Delay(TimeSpan delay)
		{
			var millisecondsDelay = (long)delay.TotalMilliseconds;

			if (millisecondsDelay > int.MaxValue)
			{
				throw new ArgumentOutOfRangeException(nameof(delay));
			}

			return Delay((int)millisecondsDelay);
		}

		/// <summary>
		/// tt
		/// </summary>
		/// <param name="waitHandle"></param>
		/// <param name="asyncResult"></param>
		/// <returns></returns>
		public static EventWaitHandle TryCreateAsyncWaitHandle(ref EventWaitHandle waitHandle, IAsyncResult asyncResult)
		{
			if (waitHandle == null)
			{
				var done = asyncResult.IsCompleted;
				var mre = new ManualResetEvent(done);

				if (Interlocked.CompareExchange(ref waitHandle, mre, null) != null)
				{
					// Another thread created this object's event; dispose the event we just created.
					mre.Close();
				}
				else if (!done && asyncResult.IsCompleted)
				{
					// We published the event as unset, but the operation has subsequently completed;
					// set the event state properly so that callers do not deadlock.
					waitHandle.Set();
				}
			}

			return waitHandle;
		}

		#endregion

		#region internals

		internal const int StatusCreated = 0;
		internal const int StatusScheduled = 1;
		internal const int StatusRunning = 2;
		internal const int StatusRanToCompletion = 3;
		internal const int StatusCanceled = 4;
		internal const int StatusFaulted = 5;

		internal bool TrySetStatus(int status, bool completedSynchronously)
		{
			if (status > StatusRunning)
			{
				status |= _flagCompleted;

				if (completedSynchronously)
				{
					status |= _flagSynchronous;
				}
			}

			return TrySetStatusInternal(status);
		}

		#endregion

		#region IAsyncContinuationContainer

		/// <inheritdoc/>
		public void AddCompletionCallback(Action action)
		{
			ThrowIfDisposed();

			if (action == null)
			{
				throw new ArgumentNullException(nameof(action));
			}

			if (IsCompleted || !TryAddContinuation(action))
			{
				action.Invoke();
			}
		}

		/// <inheritdoc/>
		public void RemoveCompletionCallback(Action action)
		{
			ThrowIfDisposed();
			throw new NotImplementedException();
		}

		#endregion

		#region IAsyncOperationCompletionSource

		/// <inheritdoc/>
		public void SetCanceled(bool completedSynchronously)
		{
			if (!TrySetCanceled(completedSynchronously))
			{
				throw new InvalidOperationException();
			}
		}

		/// <inheritdoc/>
		public bool TrySetCanceled(bool completedSynchronously)
		{
			ThrowIfDisposed();

			if (TrySetStatus(StatusCanceled, completedSynchronously))
			{
				OnCompleted();
				return true;
			}

			return false;
		}

		/// <inheritdoc/>
		public void SetException(Exception e, bool completedSynchronously)
		{
			if (!TrySetException(e, completedSynchronously))
			{
				throw new InvalidOperationException();
			}
		}

		/// <inheritdoc/>
		public bool TrySetException(Exception e, bool completedSynchronously)
		{
			ThrowIfDisposed();

			var status = e is OperationCanceledException ? StatusCanceled : StatusFaulted;

			if (TrySetStatus(status, completedSynchronously))
			{
				_exception = e;
				OnCompleted();
				return true;
			}

			return false;
		}

		/// <inheritdoc/>
		public void SetCompleted(bool completedSynchronously)
		{
			if (!TrySetCompleted(completedSynchronously))
			{
				throw new InvalidOperationException();
			}
		}

		/// <inheritdoc/>
		public bool TrySetCompleted(bool completedSynchronously)
		{
			ThrowIfDisposed();

			if (TrySetStatus(StatusRanToCompletion, completedSynchronously))
			{
				OnCompleted();
				return true;
			}

			return false;
		}

		#endregion

		#region IAsyncOperation

		/// <inheritdoc/>
		public AsyncOperationStatus Status => (AsyncOperationStatus)(_flags & _statusMask);

		/// <inheritdoc/>
		public Exception Exception => _exception;

		/// <inheritdoc/>
		public bool IsCompletedSuccessfully => (_flags & _statusMask) == StatusRanToCompletion;

		/// <inheritdoc/>
		public bool IsFaulted => (_flags & _statusMask) == StatusFaulted;

		/// <inheritdoc/>
		public bool IsCanceled => (_flags & _statusMask) == StatusCanceled;

		#endregion

		#region IAsyncResult

		/// <inheritdoc/>
		public WaitHandle AsyncWaitHandle
		{
			get
			{
				ThrowIfDisposed();
				return TryCreateAsyncWaitHandle(ref _waitHandle, this);
			}
		}

		/// <inheritdoc/>
		public object AsyncState => _asyncState;

		/// <inheritdoc/>
		public bool CompletedSynchronously => (_flags & _flagSynchronous) != 0;

		/// <inheritdoc/>
		public bool IsCompleted => (_flags & _flagCompleted) != 0;

		#endregion

		#region IEnumerator

		/// <inheritdoc/>
		public object Current => null;

		/// <inheritdoc/>
		public bool MoveNext() => _flags == StatusRunning;

		/// <inheritdoc/>
		public void Reset() => throw new NotSupportedException();

		#endregion

		#region IDisposable

		/// <inheritdoc/>
		public void Dispose()
		{
			if (!IsCompleted)
			{
				throw new InvalidOperationException("Cannot dispose non-completed operation.");
			}

			Dispose(true);
			GC.SuppressFinalize(this);
		}

		#endregion

		#region Object

		/// <inheritdoc/>
		public override string ToString()
		{
			return GetType().Name;
		}

		#endregion

		#region implementation

		private string DebuggerDisplay
		{
			get
			{
				var result = ToString();
				var state = Status.ToString();

				if (IsFaulted && _exception != null)
				{
					state += " (" + _exception.GetType().Name + ')';
				}

				result += ", Status = ";
				result += state;

				if (IsDisposed)
				{
					result += ", Disposed";
				}

				return result;
			}
		}

		private AsyncResult(int flags)
		{
			_flags = flags;
		}

		private bool TrySetStatusInternal(int newStatus)
		{
			var status = _flags;

			if ((status & _flagCompleted) == 0)
			{
				var status0 = status & _statusMask;
				var status1 = newStatus & _statusMask;

				if (status0 < status1)
				{
					if (Interlocked.CompareExchange(ref _flags, newStatus, status) == status)
					{
						OnStatusChanged();
						return true;
					}
				}
			}

			return false;
		}

		private bool TryAddContinuation(object d1)
		{
			// NOTE: the code below is adapted from https://referencesource.microsoft.com/#mscorlib/system/threading/Tasks/Task.cs.
			var d0 = _continuation;

			// If no continuation is stored yet, try to store it as _continuation.
			if (d0 == null)
			{
				d0 = Interlocked.CompareExchange(ref _continuation, d1, null);

				// Quick return if exchange succeeded.
				if (d0 == null)
				{
					return true;
				}
			}

			// Logic for the case where we were previously storing a single continuation.
			if (d0 != _continuationCompletionSentinel && !(d0 is ArrayList))
			{
				var newList = new ArrayList() { d0 };

				Interlocked.CompareExchange(ref _continuation, newList, d0);

				// We might be racing against another thread converting the single into a list,
				// or we might be racing against operation completion, so resample "list" below.
			}

			// If list is null, it can only mean that _continuationCompletionSentinel has been exchanged
			// into _continuation. Thus, the task has completed and we should return false from this method,
			// as we will not be queuing up the continuation.
			if (_continuation is ArrayList list)
			{
				lock (list)
				{
					// It is possible for the operation to complete right after we snap the copy of the list.
					// If so, then fall through and return false without queuing the continuation.
					if (_continuation != _continuationCompletionSentinel)
					{
						list.Add(d1);
						return true;
					}
				}
			}

			return false;
		}

		private void InvokeContinuation()
		{
			var continuation = Interlocked.Exchange(ref _continuation, _continuationCompletionSentinel);

			if (continuation != null)
			{
				if (continuation is IEnumerable list)
				{
					lock (list)
					{
						foreach (var item in list)
						{
							InvokeContinuation(item);
						}
					}
				}
				else
				{
					InvokeContinuation(continuation);
				}
			}
		}

		private void InvokeContinuation(object continuation)
		{
			if (continuation is Action a)
			{
				a.Invoke();
			}
			else if (continuation is AsyncCallback ac)
			{
				ac.Invoke(this);
			}
		}

		#endregion
	}
}
