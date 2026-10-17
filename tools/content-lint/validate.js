'use strict';

/**
 * Minimal, dependency-free JSON Schema (draft 2020-12) validator.
 *
 * Supported keywords: $ref (local only), $defs, type, enum, const, pattern,
 * minLength, maxLength, minimum, maximum, minItems, maxItems, uniqueItems,
 * items, properties, required, additionalProperties, allOf, anyOf, oneOf,
 * if/then/else, format (date-time only, validated).
 *
 * Deliberately not a full implementation: it covers exactly the subset used by
 * content/schema/word.schema.json, so tools/content-lint stays zero-install.
 */

const SUPPORTED_KEYWORDS = new Set([
  '$schema', '$id', 'title', 'description', '$comment', '$ref', '$defs',
  'type', 'enum', 'const', 'pattern', 'minLength', 'maxLength',
  'minimum', 'maximum', 'minItems', 'maxItems', 'uniqueItems', 'items',
  'properties', 'required', 'additionalProperties',
  'allOf', 'anyOf', 'oneOf', 'if', 'then', 'else', 'format'
]);

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function typeOf(value) {
  if (value === null) return 'null';
  if (Array.isArray(value)) return 'array';
  if (Number.isInteger(value)) return 'integer';
  return typeof value;
}

function matchesType(value, type) {
  switch (type) {
    case 'integer': return Number.isInteger(value);
    case 'number': return typeof value === 'number';
    case 'null': return value === null;
    case 'array': return Array.isArray(value);
    case 'object': return isObject(value);
    default: return typeof value === type;
  }
}

function deepEqual(a, b) {
  return JSON.stringify(a) === JSON.stringify(b);
}

function resolveRef(ref, root) {
  if (typeof ref !== 'string' || !ref.startsWith('#/')) {
    throw new Error(`unsupported $ref (only local refs allowed): ${ref}`);
  }
  return ref
    .slice(2)
    .split('/')
    .reduce(
      (node, key) =>
        node === undefined
          ? node
          : node[key.replace(/~1/g, '/').replace(/~0/g, '~')],
      root
    );
}

/**
 * @param {object} schema
 * @param {unknown} data
 * @param {object} root       the whole schema, for $ref resolution
 * @param {string} path       JSON pointer-ish path for messages
 * @param {string[]} errors   collector
 */
function validate(schema, data, root, path, errors) {
  if (schema.$ref) {
    return validate(resolveRef(schema.$ref, root), data, root, path, errors);
  }
  const at = path || '$';

  if (schema.type !== undefined) {
    const types = Array.isArray(schema.type) ? schema.type : [schema.type];
    if (!types.some((t) => matchesType(data, t))) {
      errors.push(`${at}: expected type ${types.join('|')}, got ${typeOf(data)}`);
      return;
    }
  }

  if (schema.const !== undefined && !deepEqual(data, schema.const)) {
    errors.push(`${at}: expected const ${JSON.stringify(schema.const)}, got ${JSON.stringify(data)}`);
  }

  if (schema.enum !== undefined && !schema.enum.some((e) => deepEqual(e, data))) {
    errors.push(`${at}: value ${JSON.stringify(data)} is not one of [${schema.enum.join(', ')}]`);
  }

  if (typeof data === 'string') {
    if (schema.minLength !== undefined && data.length < schema.minLength) {
      errors.push(`${at}: length ${data.length} < minLength ${schema.minLength}`);
    }
    if (schema.maxLength !== undefined && data.length > schema.maxLength) {
      errors.push(`${at}: length ${data.length} > maxLength ${schema.maxLength}`);
    }
    if (schema.pattern !== undefined && !new RegExp(schema.pattern, 'u').test(data)) {
      errors.push(`${at}: does not match pattern ${schema.pattern}`);
    }
    if (schema.format === 'date-time' && Number.isNaN(Date.parse(data))) {
      errors.push(`${at}: not a valid date-time: ${JSON.stringify(data)}`);
    }
  }

  if (typeof data === 'number') {
    if (schema.minimum !== undefined && data < schema.minimum) {
      errors.push(`${at}: ${data} < minimum ${schema.minimum}`);
    }
    if (schema.maximum !== undefined && data > schema.maximum) {
      errors.push(`${at}: ${data} > maximum ${schema.maximum}`);
    }
  }

  if (Array.isArray(data)) {
    if (schema.minItems !== undefined && data.length < schema.minItems) {
      errors.push(`${at}: ${data.length} items < minItems ${schema.minItems}`);
    }
    if (schema.maxItems !== undefined && data.length > schema.maxItems) {
      errors.push(`${at}: ${data.length} items > maxItems ${schema.maxItems}`);
    }
    if (schema.uniqueItems) {
      for (let i = 0; i < data.length; i += 1) {
        for (let j = i + 1; j < data.length; j += 1) {
          if (deepEqual(data[i], data[j])) errors.push(`${at}[${j}]: duplicate item`);
        }
      }
    }
    if (schema.items) {
      data.forEach((item, i) => validate(schema.items, item, root, `${at}[${i}]`, errors));
    }
  }

  if (isObject(data)) {
    const props = schema.properties || {};
    for (const req of schema.required || []) {
      if (!(req in data)) errors.push(`${at}: missing required property "${req}"`);
    }
    for (const key of Object.keys(data)) {
      const propSchema = props[key];
      if (propSchema !== undefined) {
        validate(propSchema, data[key], root, `${at}.${key}`, errors);
      } else if (schema.additionalProperties === false) {
        errors.push(`${at}: additional property "${key}" is not allowed`);
      } else if (isObject(schema.additionalProperties)) {
        validate(schema.additionalProperties, data[key], root, `${at}.${key}`, errors);
      }
    }
  }

  if (Array.isArray(schema.allOf)) {
    for (const sub of schema.allOf) validate(sub, data, root, path, errors);
  }

  if (Array.isArray(schema.anyOf)) {
    const ok = schema.anyOf.some((sub) => {
      const e = [];
      validate(sub, data, root, path, e);
      return e.length === 0;
    });
    if (!ok) errors.push(`${at}: does not match any anyOf branch`);
  }

  if (Array.isArray(schema.oneOf)) {
    const n = schema.oneOf.filter((sub) => {
      const e = [];
      validate(sub, data, root, path, e);
      return e.length === 0;
    }).length;
    if (n !== 1) errors.push(`${at}: matches ${n} oneOf branches (needs exactly 1)`);
  }

  if (schema.if !== undefined) {
    const e = [];
    validate(schema.if, data, root, path, e);
    if (e.length === 0) {
      if (schema.then !== undefined) validate(schema.then, data, root, path, errors);
    } else if (schema.else !== undefined) {
      validate(schema.else, data, root, path, errors);
    }
  }
}

module.exports = { validate, SUPPORTED_KEYWORDS };
