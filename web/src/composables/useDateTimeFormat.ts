import { ref } from 'vue'
import { api } from '../api/client'
import { DEFAULT_DATE_TIME_FORMAT, formatDateTime, toDateTimeFormatId, type DateTimeFormatId } from '../dateTime'

/**
 * Configuration ▸ General ▸ "Date and time format": how a date is written on a screenshot's date
 * line and in a report's "Taken" and "Generated" lines. Module-level singleton like useAutosave and
 * stored the same way (`ui.dateTimeFormat` in the server's configuration database): a document
 * that leaves this PC should look the same whichever browser tab produced it.
 *
 * `formatWithPreference` is what the report builder and the screenshot call — a plain function
 * over the singleton, so a module with no component context (buildReport.ts) can use it. It reads
 * whatever is loaded at the time; App.vue kicks `load()` off at startup so a report built seconds
 * in already has the stored choice, and until then the system locale is what it always was.
 */
const dateTimeFormat = ref<DateTimeFormatId>(DEFAULT_DATE_TIME_FORMAT)
let loadPromise: Promise<void> | null = null

export function formatWithPreference(date: Date): string {
  return formatDateTime(date, dateTimeFormat.value)
}

export function useDateTimeFormat() {
  async function load() {
    if (loadPromise) return loadPromise
    loadPromise = (async () => {
      try {
        dateTimeFormat.value = toDateTimeFormatId((await api.getDateTimeFormatConfig()).format)
      } catch {
        // The system locale is the server's default too; nothing to show an error on yet.
      }
    })()
    return loadPromise
  }

  async function setDateTimeFormat(format: DateTimeFormatId) {
    dateTimeFormat.value = format
    try {
      await api.setDateTimeFormatConfig({ format })
    } catch {
      // Applied on screen already; a failed write is found out next session, which for a
      // preference is the right trade.
    }
  }

  return { dateTimeFormat, load, setDateTimeFormat, format: formatWithPreference }
}
