﻿/*
AdamMil.Collections is a library that provides useful collection classes for
the .NET framework.

http://www.adammil.net/
Copyright (C) 2007-2011 Adam Milazzo

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.
This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
*/

using System;

// TODO: is it possible to generalize this for 64-bit platforms using some kind of native integer type? C# and/or the CLR seems
// to optimize IntPtr in release builds, but in debug builds it's slow. perhaps we could use void* instead of IntPtr to get
// decent code in both debug and release builds? (or, if it's C# doing the optimizing, we could turn on optimizations for debug
// builds)

namespace AdamMil.Collections
{

/// <summary>Implements a Bloom filter, which is a space-efficient probabilistic set that supports two operations. You can add
/// an item to the set using <see cref="Add"/>, and you can query whether an item might have been added to the set using
/// <see cref="PossiblyContains"/>. False positives are possible, so that <see cref="PossiblyContains"/> may return true for an
/// item that was never added, but false negatives are not possible, so if <see cref="PossiblyContains"/> returns false, then the
/// item definitely was not added. It is not possible to remove items from the set or to count or enumerate the items within the
/// set.
/// </summary>
/// <remarks>
/// Bloom filters are most efficient when the set of keys that will be added is sparse with respect to the set of possible keys,
/// and the approximate number of items that will be added is known in advance. At a 0.1% false positive rate, the filter uses
/// about 14.4 bits per expected item, depending on the number of hash functions. (With an optimal number of hash functions, the
/// number of bits per expected item equals <c>ln(p) / ln(2)^2</c> where p is the probabity of false positives.)
/// So if the filter will be used with 100 randomly selected 32-bit integers, then it is an efficient data structure.
/// But if it will be used with 100 integers from the restricted range of 1000 to 1499, then a
/// Bloom filter is a poor choice. A better choice would simply be a bit array containing 500 bits, with a single bit
/// representing each integer from 1000 to 1499. This uses only 1 bit per item (5 bits per expected item), has no false
/// positives, allows more items to be stored, allows the items in the set to be enumerated and counted, and is much faster.
/// <para>Bloom filters have a size that is fixed when they are created. As more items are added to the set, the false positive
/// rate increases. It is possible to tune the size of the Bloom filter to achieve an expected false positive rate for a given
/// number of items. The <see cref="BloomFilter{T}(int,float)"/> constructor can assist with this.
/// </para>
/// <para>
/// The Bloom filter requires an <see cref="IMultiHashProvider{T}"/> to operate, although the <see cref="MultiHashProvider{T}"/>
/// class, which is used by default, should be suitable in most cases.
/// </para>
/// </remarks>
public class BloomFilter<T>
{
  /// <include file="documentation.xml" path="/Collections/BloomFilter/TuningConstructor/*[@name != 'hashProvider' and @name != 'maxHashCount']"/>
  public BloomFilter(int itemCount, float falsePositiveRate) : this(itemCount, falsePositiveRate, null, 0) { }

  /// <include file="documentation.xml" path="/Collections/BloomFilter/TuningConstructor/*[@name != 'maxHashCount']"/>
  public BloomFilter(int itemCount, float falsePositiveRate, IMultiHashProvider<T> hashProvider)
    : this(itemCount, falsePositiveRate, hashProvider, 0) { }

  /// <include file="documentation.xml" path="/Collections/BloomFilter/TuningConstructor/*[@name != 'hashProvider']"/>
  public BloomFilter(int itemCount, float falsePositiveRate, int maxHashCount)
    : this(itemCount, falsePositiveRate, null, maxHashCount) { }

  /// <include file="documentation.xml" path="/Collections/BloomFilter/TuningConstructor/*"/>
  public BloomFilter(int itemCount, float falsePositiveRate, IMultiHashProvider<T> hashProvider, int maxHashCount)
  {
    if(itemCount < 0 || falsePositiveRate <= 0 || falsePositiveRate >= 1 || maxHashCount < 0)
    {
      throw new ArgumentOutOfRangeException();
    }
    if(itemCount == 0) itemCount = 1; // prevent a bit count of zero
    if(hashProvider == null) hashProvider = MultiHashProvider<T>.Default;
    if(hashProvider.HashCount == 0) throw new ArgumentException("The hash provider does not support any hash functions.");
    maxHashCount = maxHashCount == 0 ? hashProvider.HashCount : Math.Min(maxHashCount, hashProvider.HashCount);

    // assuming an optimal number of hash functions, the required bit count is -(itemCount * ln(falsePositiveRate) / ln(2)^2).
    // we'll convert the division into multiplication by the reciprocal (1 / ln(2)^2) ~= 2.08, and then factor in the negation
    double doubleBitCount = Math.Log(falsePositiveRate) * itemCount * -2.0813689810056;

    // given that number of bits, the optimal number of hash functions is bitCount * ln(2) / itemCount. we'll round the result
    int hashCount = (int)Math.Min(maxHashCount, (long)(doubleBitCount * 0.6931471805599 / itemCount + 0.5));

    // since the hash count wasn't an exact integer, and may have been clipped by hashProvider.HashCount, we'll recalculate the
    // bit count to be optimal for the actual number of hash functions. the optimal number of bits for a given false positive
    // rate and number of hash functions can be computed using the following formula:
    // falsePositiveRate ~= (1 - e^(-hashCount * itemCount / bitCount)) ^ hashCount
    // if we use p = falsePositiveRate, k = hashCount, n = itemCount, and m = bitCount, we get the following transformation:
    // p ~= (1 - e^(-kn/m))^k
    // p^(1/k) ~= 1 - e^(-kn/m)
    // 1 - p^(1/k) ~= e^(-kn/m)
    // ln(1 - p^(1/k)) ~= -kn / m
    // -kn / ln(1 - p^(1/k)) ~= m
    //
    // is there a way to avoid calculating the bit count twice?
    long bitCount = (long)Math.Round((double)-hashCount * itemCount /
                                Math.Log(1 - Math.Pow(falsePositiveRate, 1.0 / hashCount)) + 0.5); // round the result up
    // 4294967264 is the largest number of bits that won't round up to a number greater than 2^32 when we do the rounding below
    if(bitCount > 4294967264) throw new ArgumentException("Too many bits (" + bitCount.ToString() + ") would be required.");

    this.bits         = new uint[(int)(bitCount/32 + ((bitCount&31) == 0 ? 0 : 1))]; // round up to the nearest 32 bits
    this.hashProvider = hashProvider;
    this.hashCount    = (int)hashCount;
  }

  /// <include file="documentation.xml" path="/Collections/BloomFilter/DirectConstructor/*"/>
  public BloomFilter(int bitCount, int maxHashCount) : this(bitCount, maxHashCount, null) { }

  /// <include file="documentation.xml" path="/Collections/BloomFilter/DirectConstructor/*"/>
  /// <param name="hashProvider">The <see cref="IMultiHashProvider{T}"/> that provides the hash codes for the items added to the
  /// set, or null to use the default hash code provider.
  /// </param>
  public BloomFilter(int bitCount, int maxHashCount, MultiHashProvider<T> hashProvider)
  {
    if(bitCount <= 0 || maxHashCount <= 0) throw new ArgumentOutOfRangeException();
    if(hashProvider == null) hashProvider = MultiHashProvider<T>.Default;
    if(hashProvider.HashCount == 0) throw new ArgumentException("The hash provider does not support any hash functions.");

    this.bits         = new uint[bitCount/32 + ((bitCount&31) == 0 ? 0 : 1)]; // round up to the nearest 32 bits
    this.hashProvider = hashProvider;
    this.hashCount    = Math.Min(hashProvider.HashCount, maxHashCount);
  }

  /// <summary>Adds an item to the set.</summary>
  public void Add(T item)
  {
    uint bitCount = (uint)bits.Length << 5; // the bit count is the array length times 32, since each uint has 32 bits
    for(int hashFunction=0; hashFunction < hashCount; hashFunction++)
    {
      // we'll get the value from each hash function and use it as an index into the bit array, setting the corresponding bits
      uint hash = (uint)hashProvider.GetHashCode(hashFunction, item) % bitCount;
      bits[hash >> 5] |= (uint)1 << (int)(hash & 31);
    }
  }

  /// <summary>Checks whether an item might have been added to the set before.</summary>
  /// <returns>True if the item might have been added to the set, or false if the item was definitely not added to the set.</returns>
  /// <remarks>Note that false positives are possible, so this method may return true for an item that was never added.</remarks>
  public bool PossiblyContains(T item)
  {
    uint bitCount = (uint)bits.Length << 5; // the bit count is the array length times 32, since each uint has 32 bits
    for(int hashFunction=0; hashFunction < hashCount; hashFunction++)
    {
      // we'll get the value from each hash function and use it as an index into the bit array. the item has not been added if
      // any bit is zero, since Add() would have set all of those bits to one
      uint hash = (uint)hashProvider.GetHashCode(hashFunction, item) % bitCount;
      if((bits[hash >> 5] & ((uint)1 << (int)(hash & 31))) == 0) return false;
    }
    return true; // all of the bits were one, so the item might have been added (or this may be a false positive)
  }

  readonly uint[] bits;
  readonly IMultiHashProvider<T> hashProvider;
  readonly int hashCount;
}

} // namespace AdamMil.Collections