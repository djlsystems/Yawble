#!/usr/bin/env python3
"""A first draft of a cover letter, for the Writer to finish.

    python3 make-cover-letter.py '<posting JSON>' <resume path>

Prints Markdown. It reads nothing but its arguments and the resume's file name, and writes nothing:
the Writer saves the finished letter itself.
"""
import json
import os
import sys


def main(argv):
    if len(argv) != 3:
        sys.stderr.write(__doc__)
        return 2

    posting = json.loads(argv[1])
    resume = os.path.basename(argv[2])
    title = posting.get("title", "the role")
    company = posting.get("company", "your company")

    print(f"# Application: {title}, {company}")
    print()
    print(f"Dear {company} hiring team,")
    print()
    print(f"I am writing to apply for the {title} role"
          + (f" ({posting['url']})" if posting.get("url") else "") + ".")
    print()
    print(f"<!-- Two resume items that fit this role best, from {resume}. -->")
    print()
    print("<!-- Why this role, in one paragraph. -->")
    print()
    print("Yours sincerely,")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
