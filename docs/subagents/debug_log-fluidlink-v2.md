# FluidLink v2 Debug Log

| Issue | Root cause | Fix |
| --- | --- | --- |
| Nested `target_frame_us` scaled by 1,000 | value lookup discarded the selected unit key | lookup now returns value and key; four aliases have regression tests |
| Fatal peer error kept session | catch filter preserved every numeric peer error | only `RuntimeEventRejected` is recoverable; sequence/session tests assert invalidation |
| Malformed bytes reported adapter rejection | decode and adapter shared one broad `try` | decode errors return `InvalidPayload` before adapter execution |
| Golden surface incomplete | fixture had only four frames | fixture expanded to 17 complete cross-language frames |
| Python silently coerced identifiers | `str()` accepted arbitrary objects | identifiers/list items are strict strings; resource release rejects register fields |
| Explicit falsy values selected defaults | `or` treated invalid values as absent | defaults now apply only to missing/`None` fields |
| Handshake encoder/decoder disagreed | Python encoders skipped capability/subset/limit validation | Hello/Welcome now enforce the same masks and bounds as .NET and Python decode |
| Vector docs overclaimed coverage | 17 frames were described as every registry value | wording now distinguishes complete message/event coverage from sampled decision/error values |
