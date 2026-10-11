import { defineBoot } from '#q-app'
import { guardNumberWheel } from '../lib/numberWheel'

/** Number boxes do not change on the mouse wheel, in every form; the reason is in `lib/numberWheel.ts`. */
export default defineBoot(() => {
  if (typeof document !== 'undefined') guardNumberWheel(document)
})
