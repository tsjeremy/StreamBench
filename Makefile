# ============================================================
# STREAM Memory Bandwidth Benchmark - Makefile (Linux / macOS)
# ============================================================
# Targets:
#   make stream_c.exe     - CPU version (C, OpenMP)
#   make stream_gpu.exe   - GPU version (OpenCL, dynamically loaded)
#   make stream_f.exe     - CPU version (Fortran, OpenMP)
#   make all              - Build all targets
#   make clean            - Remove executables and object files
#
# Notes:
#   - For Windows, use cl.exe instead (see README.md)
#   - GPU version does NOT need OpenCL SDK — it loads the library
#     at runtime via dlopen. Only GPU drivers are required.
#   - On macOS, you may need: brew install libomp
#     and use: CC=clang CFLAGS="-O2 -Xpreprocessor -fopenmp -lomp"
# ============================================================

CC = gcc
CFLAGS = -O2 -fopenmp

FC = gfortran
FFLAGS = -O2 -fopenmp

all: stream_f.exe stream_c.exe stream_gpu.exe

# CPU version (C with OpenMP)
stream_c.exe: stream.c
	$(CC) $(CFLAGS) stream.c -o stream_c.exe

# GPU version (OpenCL loaded dynamically — no SDK needed)
# Linux: needs -ldl for dlopen. macOS: use -lm only (no -ldl needed).
stream_gpu.exe: stream_gpu.c
	$(CC) -O2 -o stream_gpu.exe stream_gpu.c -ldl -lm

# CPU version (Fortran with OpenMP)
stream_f.exe: stream.f mysecond.o
	$(CC) $(CFLAGS) -c mysecond.c
	$(FC) $(FFLAGS) -c stream.f
	$(FC) $(FFLAGS) stream.o mysecond.o -o stream_f.exe

clean:
	rm -f stream_f.exe stream_c.exe stream_gpu.exe *.o

# an example of a more complex build line for the Intel icc compiler
stream.icc: stream.c
	icc -O3 -xCORE-AVX2 -ffreestanding -qopenmp -DSTREAM_ARRAY_SIZE=80000000 -DNTIMES=20 stream.c -o stream.omp.AVX2.80M.20x.icc
