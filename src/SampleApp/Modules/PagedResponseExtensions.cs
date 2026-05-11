namespace SampleApp.Modules;

public static class PagedResponseExtensions
{
    /// <summary>
    /// Projects a materialized list of read models into a <see cref="PagedResponse{TView}"/>.
    /// </summary>
    /// <param name="items">Items for the current page. Typically materialized via <c>ToListAsync()</c>.</param>
    /// <param name="total">Total number of matching items across all pages.</param>
    /// <param name="itemLinks">
    /// Optional factory producing HATEOAS <see cref="Link"/>s for each item.
    /// Omit for endpoints that do not add per-item links.
    /// </param>
    /// <param name="links">Optional page-level HATEOAS links. Defaults to empty.</param>
    public static PagedResponse<TView> ToPagedResponse<TView>(
        this IEnumerable<TView> items,
        int total,
        Func<TView, Link[]>? itemLinks = null,
        Link[]? links = null)
        where TView : ReadModel
        => new(
            Items: items.Select(i => new ViewResponse<TView>(i, itemLinks?.Invoke(i) ?? [])),
            Count: items.Count(),
            Total: total,
            Links: links ?? []);
}
