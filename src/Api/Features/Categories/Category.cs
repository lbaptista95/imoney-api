namespace Api.Features.Categories;

/// <summary>
/// A transaction category. This feature creates only the table and the optional
/// foreign key from transactions; there is no endpoint and no categorization rule
/// yet, both of which belong to the categorization feature (M4).
/// </summary>
public sealed class Category
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
