// Готовые аватары: можно выбрать вместо загрузки своего фото. Файлы лежат в public/avatars.
export const AVATAR_PRESETS = [
  { key: 'boy', url: '/avatars/boy.webp' },
  { key: 'girl', url: '/avatars/girl.webp' },
] as const

// Выбранная картинка загружается на сервер тем же запросом, что и своё фото (PUT /me/avatar)
export async function presetToFile(url: string): Promise<File> {
  const res = await fetch(url)
  const blob = await res.blob()
  return new File([blob], url.split('/').pop() ?? 'avatar.webp', { type: 'image/webp' })
}

