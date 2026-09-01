# Fonts

One face, embedded because it is not on a stock machine and the wordmark is the
one place this application speaks in a voice that is not the desktop's.

| File | Family | Role |
| --- | --- | --- |
| `Bevan-Italic.ttf` | Bevan | The wordmark, and nothing else. It is a poster slab, unreadable below about 20px and never allowed near a control. Only the italic ships — it is the only cut used. |

Licensed under the [SIL Open Font License 1.1][ofl], which permits bundling it
inside an application. It is an unmodified release from Google Fonts, by Vernon
Adams and Cyreal.

Everything else — body text and the caption role alike — is the platform's own
UI face, in both themes, named as `$Default`.

## Why the text face is not embedded

Cyrk 66 shipped Space Grotesk for a while, on the argument that a design system
falling back to Arial is not applied at all. That argument is sound for a
display face seen once at 44px and does not survive contact with the body of a
desktop application.

Two measurements settled it. Space Grotesk's x-height is **0.486** of its em,
against **0.508** for the macOS system UI face — so at any given size its
lowercase renders about 4% smaller, which is close enough to look like a mistake
rather than a choice, and was a real part of why the captions read as
undersized. Its cap-height is **0.700** against **0.705**: all but identical.
Since this theme's voice is carried by uppercase tracked captions, the identity
was riding on the half of the face the theme barely uses.

A system UI face is also hinted and optically sized for screen text at 12px,
which a display-leaning grotesque is not, and it makes the two themes agree
about what body text is. A theme here is a palette and a geometry, not a second
opinion on legibility.

## Why there is no monospace either

The Cyrk 66 kit sets its caption role in Space Mono. That role is perhaps a
tenth of a marketing page; here it is closer to half of what is on screen, and
at caption sizes a fixed-width face is measurably slower to read than a
grotesque doing the same work. What the role actually needs is to be
unmistakably not-body-text, and small, uppercase, tracked and secondary-coloured
already says so four times over. The source poster is hand-lettered — there is
no mono anywhere in it.

The one thing a mono was doing that a text face could not is keeping numbers
aligned, and OpenType answers that: `FontFeatures="tnum"` on the roles carrying
counts, dates and sizes. Note that this now depends on the machine — the macOS
system face and Segoe UI both carry `tnum`, but Helvetica has no OpenType
features at all and ignores it silently. The request costs nothing where it is
not understood.

`FontMono` survives as a resource key, pointing at the platform's own mono, for
the file-path lists in the error banners — the one place fixed width earns its
keep, where telling `l` from `1` matters and the string is a machine artifact
you might copy. `TypographyTests` pins that it stays a platform stack.

## What this costs

Only embedded faces can be verified. The headless font manager ships five stub
families and resolves every system font name to `BareMinimum`, so
`TypographyTests` can prove Bevan arrives and cannot prove anything about the
text face. That a theme is wearing the body font it claims is now something only
a person looking at the running application can confirm.

[ofl]: https://openfontlicense.org/
