﻿// Copyright (c) Alexander Bogarsukov.
// Licensed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Threading;

namespace UnityFx.Async
{
	/// <summary>
	/// References a method to be called when a corresponding operation completes.
	/// </summary>
	/// <param name="op">The asynchronous operation.</param>
	/// <seealso cref="IAsyncOperationEvents"/>
	/// <seealso cref="IAsyncOperation"/>
	public delegate void AsyncOperationCallback(IAsyncOperation op);

	/// <summary>
	/// A controller for <see cref="IAsyncOperation"/> completion callbacks.
	/// </summary>
	/// <seealso cref="IAsyncOperation"/>
	public interface IAsyncOperationEvents
	{
		/// <summary>
		/// Raised when the operation has completed.
		/// </summary>
		/// <remarks>
		/// The event handler is invoked on a thread that initiated the operation completion (not on a thread that registered it).
		/// If the operation is already completed the event handler is not called.
		/// </remarks>
		/// <seealso cref="TryAddCompletionCallback(AsyncOperationCallback, SynchronizationContext)"/>
		/// <seealso cref="RemoveCompletionCallback(AsyncOperationCallback)"/>
		event EventHandler Completed;

		/// <summary>
		/// Attempts to add a completion callback to be executed after the operation has finished. If the operation is already completed
		/// the method does nothing and just returns <see langword="false"/>.
		/// </summary>
		/// <param name="action">The callback to be executed when the operation has completed.</param>
		/// <param name="syncContext">If not <see langword="null"/> method attempts to marshal the continuation to the synchronization context.
		/// Otherwise the callback is invoked on a thread that initiated the operation completion.
		/// </param>
		/// <returns>Returns <see langword="true"/> if the callback was added; <see langword="false"/> otherwise (the operation is completed).</returns>
		/// <exception cref="ArgumentNullException">Thrown if the <paramref name="action"/> is <see langword="null"/>.</exception>
		/// <exception cref="ObjectDisposedException">Thrown is the operation has been disposed.</exception>
		/// <seealso cref="RemoveCompletionCallback(AsyncOperationCallback)"/>
		/// <seealso cref="Completed"/>
		bool TryAddCompletionCallback(AsyncOperationCallback action, SynchronizationContext syncContext);

		/// <summary>
		/// Removes an existing completion callback.
		/// </summary>
		/// <param name="action">The callback to remove. Can be <see langword="null"/>.</param>
		/// <returns>Returns <see langword="true"/> if the <paramref name="action"/> was removed; <see langword="false"/> otherwise.</returns>
		/// <exception cref="ObjectDisposedException">Thrown is the operation has been disposed.</exception>
		/// <seealso cref="TryAddCompletionCallback(AsyncOperationCallback, SynchronizationContext)"/>
		/// <seealso cref="Completed"/>
		bool RemoveCompletionCallback(AsyncOperationCallback action);
	}
}