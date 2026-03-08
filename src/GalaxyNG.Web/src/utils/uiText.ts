export function sanitizeUiText(raw: string | null | undefined): string {
  if (!raw) return '';

  let text = raw.replace(/\r\n/g, '\n').trim();

  text = text.replace(/(?:^|\n)\s*(thinking process|analysis|reasoning)\s*:.*/gim, '');
  text = text.replace(/\n{3,}/g, '\n\n').trim();

  return text;
}
