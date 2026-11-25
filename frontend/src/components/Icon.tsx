import type { ReactNode } from 'react'

// Небольшой набор иконок, чтобы не тянуть отдельную библиотеку (её нет в ТЗ).
const icons = {
  bolt: <path d="M13 2 4 14h7l-1 8 9-12h-7z" />,
  home: <path d="M3 10.5 12 3l9 7.5V20a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z" />,
  search: (
    <>
      <circle cx="11" cy="11" r="7" />
      <path d="m20 20-3.5-3.5" />
    </>
  ),
  swap: <path d="M7 7h12l-3-3M17 17H5l3 3" />,
  message: <path d="M21 12a8 8 0 0 1-11.6 7.1L4 20l1-4.6A8 8 0 1 1 21 12z" />,
  calendar: (
    <>
      <rect x="3" y="5" width="18" height="16" rx="2" />
      <path d="M8 3v4M16 3v4M3 10h18" />
    </>
  ),
  star: <path d="m12 3 2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1L3.2 9.5l6.1-.9z" />,
  bell: <path d="M6 9a6 6 0 1 1 12 0c0 6 2 7 2 7H4s2-1 2-7M10 20a2 2 0 0 0 4 0" />,
  user: (
    <>
      <circle cx="12" cy="8" r="4" />
      <path d="M4 21a8 8 0 0 1 16 0" />
    </>
  ),
  settings: (
    <>
      <path d="M4 7h9M17 7h3M4 17h3M11 17h9" />
      <circle cx="15" cy="7" r="2" />
      <circle cx="9" cy="17" r="2" />
    </>
  ),
  menu: <path d="M4 6h16M4 12h16M4 18h16" />,
  x: <path d="M6 6l12 12M18 6 6 18" />,
  laptop: (
    <>
      <rect x="4" y="5" width="16" height="11" rx="2" />
      <path d="M2 20h20" />
    </>
  ),
  phone: (
    <>
      <rect x="7" y="2.5" width="10" height="19" rx="2.5" />
      <path d="M11 18.5h2" />
    </>
  ),
  tablet: (
    <>
      <rect x="4.5" y="2.5" width="15" height="19" rx="2.5" />
      <path d="M11 18.5h2" />
    </>
  ),
  logout: <path d="M15 17l5-5-5-5M20 12H9M12 21H5a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1h7" />,
  video: (
    <>
      <rect x="3" y="6" width="12" height="12" rx="2" />
      <path d="m15 10 6-3v10l-6-3" />
    </>
  ),
  scale: <path d="M12 3v18M6 21h12M5 7h14M5 7l-3 7a3 3 0 0 0 6 0zM19 7l-3 7a3 3 0 0 0 6 0z" />,
  code: <path d="m8 8-4 4 4 4M16 8l4 4-4 4M14 5l-4 14" />,
  check: <path d="m5 12 5 5 9-10" />,
  arrowRight: <path d="M5 12h14M13 6l6 6-6 6" />,
  chevronDown: <path d="m6 9 6 6 6-6" />,
  play: <path d="M8 5v14l11-7z" />,
  send: <path d="m22 2-11 11M22 2l-7 20-4-9-9-4z" />,
  paperclip: <path d="m21 12-8.5 8.5a5 5 0 0 1-7-7L14 5a3.5 3.5 0 0 1 5 5l-8.5 8.5a2 2 0 0 1-3-3L15 8" />,
  file: <path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8zM14 3v5h5" />,
  dashboard: (
    <>
      <rect x="3" y="3" width="7" height="9" rx="1.5" />
      <rect x="14" y="3" width="7" height="5" rx="1.5" />
      <rect x="14" y="12" width="7" height="9" rx="1.5" />
      <rect x="3" y="16" width="7" height="5" rx="1.5" />
    </>
  ),
  users: (
    <>
      <circle cx="9" cy="8" r="3.5" />
      <path d="M2.5 20a6.5 6.5 0 0 1 13 0M16 4.6a3.5 3.5 0 0 1 0 6.8M18 14.3a6.5 6.5 0 0 1 3.5 5.7" />
    </>
  ),
  ban: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="m5.6 5.6 12.8 12.8" />
    </>
  ),
  inbox: <path d="M3 13h5l1.5 3h5L16 13h5M3 13l3-8h12l3 8v6a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z" />,
  lightbulb: <path d="M9 18h6M10 21h4M12 3a6 6 0 0 0-3.5 10.9c.9.7 1.5 1.6 1.5 2.6h4c0-1 .6-1.9 1.5-2.6A6 6 0 0 0 12 3z" />,
  folder: <path d="M3 6a1 1 0 0 1 1-1h5l2 2.5h8a1 1 0 0 1 1 1V19a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z" />,
  tag: <path d="M3 12V4h8l10 10-8 8zM7.5 7.5h.01" />,
  shield: <path d="M12 3 4 6v6c0 4.5 3.2 8 8 9 4.8-1 8-4.5 8-9V6z" />,
  chevronRight: <path d="m9 6 6 6-6 6" />,
  appeal: <path d="M4 4h16v12H8l-4 4zM8 9h8M8 12h5" />,
  mic: <path d="M12 3a3 3 0 0 0-3 3v6a3 3 0 0 0 6 0V6a3 3 0 0 0-3-3zM5 11a7 7 0 0 0 14 0M12 18v3" />,
  micOff: <path d="M15 10V6a3 3 0 0 0-5.7-1.3M9 9v3a3 3 0 0 0 4.9 2.3M5 11a7 7 0 0 0 11.6 5.3M19 11a7 7 0 0 1-.6 2.8M12 18v3M3 3l18 18" />,
  videoOff: <path d="M15 11V8a2 2 0 0 0-2-2H8.5M3.7 6.3A2 2 0 0 0 3 8v8a2 2 0 0 0 2 2h8a2 2 0 0 0 1.7-1M15 10l6-3v10l-2.2-1.1M3 3l18 18" />,
  screen: <path d="M3 5h18v11H3zM8 20h8M12 16v4M9 11l3-3 3 3M12 8v6" />,
  phoneOff: <path d="M10.7 13.3a13 13 0 0 1-2-2.9l1.5-1.5a1 1 0 0 0 .2-1.2L9 4.6A1 1 0 0 0 7.8 4L4.6 5a1 1 0 0 0-.6 1.1A17 17 0 0 0 9 15M14.6 16.2a17 17 0 0 0 3.3 1.8 1 1 0 0 0 1.1-.6l1-3.2a1 1 0 0 0-.6-1.2l-3-1.4a1 1 0 0 0-1.2.2l-1 1M3 21 21 3" />,
  flag: <path d="M5 21V4M5 4h11l-2 4 2 4H5" />,
} satisfies Record<string, ReactNode>

export type IconName = keyof typeof icons

export default function Icon({ name, className = 'h-5 w-5' }: { name: IconName; className?: string }) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      aria-hidden
    >
      {icons[name]}
    </svg>
  )
}
