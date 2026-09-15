import '@testing-library/jest-dom/vitest'

// Node.js 25 ships a built-in `localStorage` stub that is non-functional when
// `--localstorage-file` is not set.  jsdom cannot override a non-configurable
// global, so we patch it with a real in-memory implementation so that any test
// that calls localStorage.getItem / setItem / removeItem / clear works correctly.
const localStorageMock = (() => {
  let store: Record<string, string> = {}
  return {
    getItem: (key: string) => store[key] ?? null,
    setItem: (key: string, value: string) => {
      store[key] = String(value)
    },
    removeItem: (key: string) => {
      delete store[key]
    },
    clear: () => {
      store = {}
    },
    get length() {
      return Object.keys(store).length
    },
    key: (index: number) => Object.keys(store)[index] ?? null,
  }
})()

Object.defineProperty(globalThis, 'localStorage', {
  value: localStorageMock,
  writable: true,
  configurable: true,
})
