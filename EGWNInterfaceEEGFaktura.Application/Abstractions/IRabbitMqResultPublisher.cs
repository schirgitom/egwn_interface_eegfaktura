namespace EGWNInterfaceEEGFaktura.Application.Abstractions;

public interface IRabbitMqResultPublisher<TMessage>
{
    Task PublishAsync(TMessage message, CancellationToken cancellationToken);
}
