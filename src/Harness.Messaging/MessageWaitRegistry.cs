using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// In-process wait registrations over appended messages.
/// </summary>
public sealed class MessageWaitRegistry
{
    private readonly object _gate = new();
    private readonly List<Registration> _registrations = [];

    public MessageWaitRegistration Register(Func<Message, bool> matches) =>
        new(this, new Registration(matches));

    public void Publish(Message message)
    {
        IReadOnlyList<Registration> matches;

        lock (_gate)
        {
            matches = _registrations.Where(registration => registration.Matches(message)).ToArray();
            foreach (var registration in matches) _registrations.Remove(registration);
        }

        foreach (var registration in matches)
        {
            registration.Source.TrySetResult(message);
        }
    }

    private void Add(Registration registration)
    {
        lock (_gate)
        {
            _registrations.Add(registration);
        }
    }

    private void Remove(Registration registration)
    {
        lock (_gate)
        {
            _registrations.Remove(registration);
        }
    }

    internal sealed class Registration(Func<Message, bool> matches)
    {
        public TaskCompletionSource<Message> Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Matches(Message message) => matches(message);
    }

    public sealed class MessageWaitRegistration : IDisposable
    {
        private readonly MessageWaitRegistry _owner;
        private readonly Registration _registration;
        private int _disposed;

        internal MessageWaitRegistration(MessageWaitRegistry owner, Registration registration)
        {
            _owner = owner;
            _registration = registration;
            _owner.Add(_registration);
        }

        public Task<Message> WaitAsync(CancellationToken ct = default) =>
            _registration.Source.Task.WaitAsync(ct);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.Remove(_registration);
        }
    }
}
