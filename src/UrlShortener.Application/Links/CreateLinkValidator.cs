using FluentValidation;

namespace UrlShortener.Application.Links;

public sealed class CreateLinkValidator : AbstractValidator<CreateLinkRequest>
{
    public CreateLinkValidator()
    {
        RuleFor(x => x.LongUrl).Cascade(CascadeMode.Stop).NotEmpty().MaximumLength(2048)
            .Must(url => url == url.Trim() && !url.Any(char.IsControl) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrWhiteSpace(uri.Host))
            .WithMessage("Only absolute HTTP or HTTPS URLs are accepted.");
        When(x => x.CustomAlias is not null, () =>
        {
            RuleFor(x => x.CustomAlias!).Cascade(CascadeMode.Stop).NotEmpty().Length(3, 30)
                .Matches(@"\A[A-Za-z0-9_-]+\z")
                .Must(alias => !new[] { "api", "health", "swagger" }.Contains(alias, StringComparer.OrdinalIgnoreCase))
                .WithMessage("The alias is reserved.");
        });
        RuleFor(x => x.ExpiresAt).Must(value => value is null || value.Value.Kind == DateTimeKind.Utc)
            .WithMessage("Expiry must use a UTC timestamp with a Z suffix.");
    }
}
