export async function copyText(value) {
    if (!navigator.clipboard || !window.isSecureContext) {
        throw new Error("The Clipboard API is unavailable.");
    }

    await navigator.clipboard.writeText(value);
}
