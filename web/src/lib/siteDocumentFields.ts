/**
 * A SITE'S DOCUMENT AS FIELDS AND VALUES. A document is whatever JSON the site stored; an object is
 * read as one line per field, a nested object's fields named by their path (`address.city`), and the
 * JSON stays one Show JSON switch away, as Admin > Log keeps its details. A document that is not an
 * object has no fields to name, so it is shown as its JSON either way.
 *
 * Every value is TEXT: the dialog interpolates it, so nothing a site stored is ever markup.
 */

export interface DocumentField {
  field: string;
  value: string;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

/** One value in words: nothing, yes or no, a list of plain values joined, anything else as JSON. */
function words(value: unknown): string {
  if (value === null || value === undefined || value === '') return 'nothing';
  if (typeof value === 'boolean') return value ? 'yes' : 'no';
  if (typeof value === 'string') return value;
  if (typeof value === 'number') return String(value);

  if (Array.isArray(value)) {
    if (value.length === 0) return 'nothing';
    if (value.every((item) => item === null || typeof item !== 'object')) return value.map(words).join(', ');
  }

  return JSON.stringify(value);
}

function collect(prefix: string, value: Record<string, unknown>, into: DocumentField[]) {
  for (const [key, inner] of Object.entries(value)) {
    const field = prefix ? `${prefix}.${key}` : key;

    if (isObject(inner) && Object.keys(inner).length > 0) collect(field, inner, into);
    else into.push({ field, value: words(isObject(inner) ? null : inner) });
  }
}

/** The document's fields in order, or null when it is not an object or has none. */
export function documentFields(doc: unknown): DocumentField[] | null {
  if (!isObject(doc)) return null;

  const fields: DocumentField[] = [];
  collect('', doc, fields);
  return fields.length > 0 ? fields : null;
}
