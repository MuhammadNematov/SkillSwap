/** @type {import('tailwindcss').Config} */
export default {
  darkMode: 'class',
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        // Тёмно-синий из логотипа
        navy: 'rgb(var(--navy) / <alpha-value>)',
        // Фиолетовая палитра из макета
        brand: {
          50: 'rgb(var(--brand-50) / <alpha-value>)',
          100: 'rgb(var(--brand-100) / <alpha-value>)',
          200: 'rgb(var(--brand-200) / <alpha-value>)',
          300: 'rgb(var(--brand-300) / <alpha-value>)',
          400: 'rgb(var(--brand-400) / <alpha-value>)',
          500: 'rgb(var(--brand-500) / <alpha-value>)',
          600: 'rgb(var(--brand-600) / <alpha-value>)',
          700: 'rgb(var(--brand-700) / <alpha-value>)',
          800: 'rgb(var(--brand-800) / <alpha-value>)',
          900: '#17143d',
        },
        surface: 'rgb(var(--surface) / <alpha-value>)',
        gold: { DEFAULT: '#C9A24B', light: '#F0D58A', dark: '#8A6A1E' },
        ink: {
          DEFAULT: 'rgb(var(--ink) / <alpha-value>)',
          soft: 'rgb(var(--ink-soft) / <alpha-value>)',
          faint: 'rgb(var(--ink-faint) / <alpha-value>)',
          line: 'rgb(var(--ink-line) / <alpha-value>)',
        },
      },
      fontFamily: {
        sans: ['Inter', 'system-ui', 'Segoe UI', 'Roboto', 'sans-serif'],
        serif: ['"Playfair Display"', 'Georgia', 'Cambria', 'Times New Roman', 'serif'],
      },
      // Сдержанные скругления: кнопки и поля 8–10px, карточки 12px
      borderRadius: {
        xl: '0.625rem',
        '2xl': '0.75rem',
        '3xl': '0.75rem',
        xl2: '0.75rem',
      },
      boxShadow: {
        card: '0 1px 3px rgba(23, 20, 61, 0.08)',
        glass: '0 1px 2px rgba(23, 20, 61, 0.04)',
      },
    },
  },
  plugins: [],
}
