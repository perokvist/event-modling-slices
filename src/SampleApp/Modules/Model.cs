using System.Text.Json.Serialization;

namespace SampleApp.Modules;

public record Event(Guid EventId);
public record Command([property: JsonIgnore] Guid Id);
public record State(Guid Id);
public record DomainEvent(Guid Id) : Event(Guid.NewGuid());
public record IntegrationEvent() : Event(Guid.NewGuid());
public record Query;
public record Query<T>() : Query where T : ReadModel?;
public record ReadModel();
public record ViewResponse<TView>(TView? View, Link[] Links) where TView : ReadModel;
public record PagedResponse<TView>(IEnumerable<ViewResponse<TView>> Items, int Count, int Total, Link[] Links) where TView : ReadModel;
public record Link(string Href, string Rel, string Method, string? Type = null);
