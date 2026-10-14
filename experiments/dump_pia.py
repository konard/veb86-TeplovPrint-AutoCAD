"""Dump the decompressed text of a PIA (PC3/PMP/CTB) file. Usage: python -I dump_pia.py <file>"""
import sys, zlib
data = open(sys.argv[1], 'rb').read()
print(data[:48])
print(zlib.decompress(data[60:]).decode('latin-1'))
