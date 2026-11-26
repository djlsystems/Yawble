// The Schemas tab's model: a schema (the JSON Schema subset workflows use: type, properties, required, items, enum, format,
// description, $ref) read as a tree of typed fields, and the Design tab's field list turned back into the same schema.
// Pure functions, so the tree and the designer's round trip are tested without a window.

export type JsonSchema = Record<string, unknown>;

/** One field as the tree shows it: its workflow type, whether it is required, its description and enum values, and the
 *  schema it refers to (directly or as a list's item), or its own fields when it is an inline object. */
export interface TreeField {
  name: string;
  type: string;
  required: boolean;
  nullable: boolean;
  description?: string;
  enum?: string[];
  ref?: string;
  children?: TreeField[];
}

/** The native types a field can have (besides list<…> and another schema's name). */
export const NATIVE_TYPES = ['string', 'int', 'float', 'bool', 'date', 'datetime', 'duration', 'any', 'object'] as const;

const isObj = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);
const clone = <T>(v: T): T => JSON.parse(JSON.stringify(v)) as T;

/** The node's JSON type, ignoring "null" in a type list (["string", "null"] is a nullable string). */
function primary(node: JsonSchema): string | undefined {
  const t = node.type;
  if (typeof t === 'string') return t;
  if (Array.isArray(t)) return (t.find((x) => x !== 'null') as string | undefined) ?? 'null';
  if (isObj(node.properties)) return 'object';
  if (node.items !== undefined) return 'array';
  return undefined;
}

const nullable = (node: JsonSchema) => Array.isArray(node.type) && node.type.includes('null');

/** A field's schema as a workflow type: string, int, datetime, list<EmailAddress>, EmailAddress, object, any. */
export function typeText(node: unknown): string {
  if (!isObj(node)) return 'any';
  if (typeof node.$ref === 'string') return node.$ref;
  switch (primary(node)) {
    case 'integer': return 'int';
    case 'number': return 'float';
    case 'boolean': return 'bool';
    case 'string':
      return node.format === 'date-time' ? 'datetime' : node.format === 'date' ? 'date' : node.format === 'duration' ? 'duration' : 'string';
    case 'array': return `list<${typeText(node.items)}>`;
    case 'object': return 'object';
    case 'null': return 'null';
    default: return Array.isArray(node.enum) ? 'string' : 'any';
  }
}

/** The schema a field refers to, directly or as a list's item (list<list<X>> too). */
function refOf(node: unknown): string | undefined {
  if (!isObj(node)) return undefined;
  if (typeof node.$ref === 'string') return node.$ref;
  return primary(node) === 'array' ? refOf(node.items) : undefined;
}

const required = (schema: JsonSchema) => new Set(Array.isArray(schema.required) ? (schema.required as unknown[]).filter((r): r is string => typeof r === 'string') : []);

/** A schema's fields as a tree, in their order; an inline object field (or a list of them) has its own fields as children. */
export function schemaTree(schema: JsonSchema, depth = 0): TreeField[] {
  const props = isObj(schema.properties) ? schema.properties : {};
  const req = required(schema);
  return Object.entries(props).map(([name, raw]) => {
    const node = isObj(raw) ? raw : {};
    const f: TreeField = { name, type: typeText(node), required: req.has(name), nullable: nullable(node) };
    if (typeof node.description === 'string' && node.description) f.description = node.description;
    if (Array.isArray(node.enum)) f.enum = node.enum.map((v) => String(v));
    const ref = refOf(node);
    if (ref) f.ref = ref;
    let inner: JsonSchema = node;
    while (primary(inner) === 'array' && isObj(inner.items)) inner = inner.items as JsonSchema;
    if (depth < 8 && isObj(inner.properties)) f.children = schemaTree(inner, depth + 1);
    return f;
  });
}

// ---- the Design tab

/** One field in the designer: what the person edits, and the field's schema as it was (kept for what the form does not show). */
export interface DesignField {
  name: string;
  type: string;
  required: boolean;
  description: string;
  /** Enum values, one per line or comma-separated; only for a string field. */
  enumValues: string;
  /** The field's schema as loaded, so keywords the form does not edit (a nullable type, an inline object's fields) survive. */
  raw?: JsonSchema;
}

/** The designer's fields of a schema. */
export function designFields(schema: JsonSchema): DesignField[] {
  const props = isObj(schema.properties) ? schema.properties : {};
  const req = required(schema);
  return Object.entries(props).map(([name, raw]) => {
    const node = isObj(raw) ? raw : {};
    return {
      name, type: typeText(node), required: req.has(name),
      description: typeof node.description === 'string' ? node.description : '',
      enumValues: Array.isArray(node.enum) ? node.enum.map((v) => String(v)).join(', ') : '',
      raw: clone(node),
    };
  });
}

export const enumList = (text: string) => text.split(/[\n,]/).map((s) => s.trim()).filter((s) => s.length > 0);

/** A workflow type as a field's schema: list<…> as an array of its item, a schema's name as a $ref. */
export function typeNode(type: string): JsonSchema {
  const t = type.trim();
  const list = /^list<(.+)>$/.exec(t);
  if (list) return { type: 'array', items: typeNode(list[1]!) };
  switch (t) {
    case 'string': return { type: 'string' };
    case 'int': return { type: 'integer' };
    case 'float': return { type: 'number' };
    case 'bool': return { type: 'boolean' };
    case 'date': return { type: 'string', format: 'date' };
    case 'datetime': return { type: 'string', format: 'date-time' };
    case 'duration': return { type: 'string', format: 'duration' };
    case 'object': return { type: 'object' };
    case 'any': case '': return {};
    default: return { $ref: t };
  }
}

/** One field's schema: its type's (or the one it had, when the type is unchanged), then its description and enum values. */
function fieldNode(f: DesignField): JsonSchema {
  const description = f.description.trim();
  const values = f.type.trim() === 'string' ? enumList(f.enumValues) : [];
  if (f.raw && typeText(f.raw) === f.type.trim()) {
    // the type is unchanged: the field's own schema, its keys in their order, with the description and enum as edited
    const node = clone(f.raw);
    if (description) node.description = description; else delete node.description;
    if (values.length) node.enum = values; else delete node.enum;
    return node;
  }
  return { ...(description ? { description } : {}), ...typeNode(f.type), ...(values.length ? { enum: values } : {}) };
}

/** The schema from the designer's fields: its own top-level keys (description, ...) kept, properties and required from the fields. */
export function schemaFromFields(base: JsonSchema, fields: readonly DesignField[]): JsonSchema {
  const out: JsonSchema = {};
  const props: Record<string, JsonSchema> = {};
  for (const f of fields) if (f.name.trim()) props[f.name.trim()] = fieldNode(f);
  const req = fields.filter((f) => f.required && f.name.trim()).map((f) => f.name.trim());
  let placed = false;
  for (const [k, v] of Object.entries(base)) {
    if (k === 'properties') { out.properties = props; placed = true; continue; }
    if (k === 'required') { if (req.length) out.required = req; continue; }
    out[k] = clone(v);
  }
  if (!out.type) out.type = 'object';
  if (!placed) out.properties = props;
  if (req.length && !('required' in out)) out.required = req;
  return out;
}

export function newField(existing: readonly DesignField[]): DesignField {
  let n = existing.length + 1;
  while (existing.some((f) => f.name === `field${n}`)) n++;
  return { name: `field${n}`, type: 'string', required: false, description: '', enumValues: '' };
}

/** The list with the field at `index` moved by `delta` (one up: -1); the same list when it cannot move. */
export function moveField<T>(list: readonly T[], index: number, delta: number): T[] {
  const to = index + delta;
  if (index < 0 || index >= list.length || to < 0 || to >= list.length) return [...list];
  const out = [...list];
  const [x] = out.splice(index, 1);
  out.splice(to, 0, x!);
  return out;
}

const NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;

/** What keeps the designer's fields from being a schema (empty: nothing): a missing, bad or repeated name, or an unknown type. */
export function designProblems(fields: readonly DesignField[], schemaNames: readonly string[]): string[] {
  const problems: string[] = [];
  const seen = new Set<string>();
  const known = new Set([...NATIVE_TYPES, ...schemaNames]);
  const typeOk = (t: string): boolean => {
    const list = /^list<(.+)>$/.exec(t.trim());
    return list ? typeOk(list[1]!) : known.has(t.trim());
  };
  fields.forEach((f, i) => {
    const n = f.name.trim();
    if (!n) problems.push(`Field ${i + 1} needs a name.`);
    else if (!NAME.test(n)) problems.push(`'${n}' is not a field name (letters, digits and _, not starting with a digit).`);
    else if (seen.has(n)) problems.push(`'${n}' is there twice.`);
    seen.add(n);
    if (!typeOk(f.type)) problems.push(`${n || `Field ${i + 1}`}: '${f.type}' is not a type (a native type, list<…> or a schema's name).`);
    if (enumList(f.enumValues).length && f.type.trim() !== 'string') problems.push(`${n}: enum values are for a string field.`);
  });
  return problems;
}

/** The types the type box suggests: the natives, each schema, and a list of each. */
export function typeSuggestions(schemaNames: readonly string[]): string[] {
  const one = [...NATIVE_TYPES.filter((t) => t !== 'any'), ...schemaNames];
  return [...one, ...one.map((t) => `list<${t}>`), 'any'];
}
