// StatReader.cpp : This file contains the 'main' function. Program execution begins and ends there.
//

#include <iostream>
#include <stdio.h>
#include <stdlib.h>
#include <windows.h>
#include <chrono>

#pragma warning(disable : 4996)

const char* const CGROUP1_MEMSTAT_FIELDS[] =
{ "total_inactive_anon  %uld\n", "total_active_anon %uld\n", "total_dirty %uld\n", "total_unevictable %uld\n" };

const char* const CGROUP1_MEMSTAT_FIELDS_2[] =
{ "total_inactive_anon ", "total_active_anon ", "total_dirty ", "total_unevictable " };

size_t s_mem_stat_n_keys = 4;

size_t ReadMemStat(const char* path)
{
    size_t total_rss = 0;
    size_t total_cache = 0;
    size_t total_active_file = 0;
    size_t total_inactive_file = 0;
    size_t result = 0;
    size_t readValues = 0;
    char line[1000];
    size_t lineLength = sizeof(line) / sizeof(char);
    FILE* file = fopen(path, "r");
    if (file == nullptr)
        goto done;
    
    char type[1000];
    char value[1000];
    char* endptr;

    while (fgets(line, lineLength, file) != NULL) 
    {
        
    }

done:
    if (readValues == 4)
    {
        std::cout << total_rss + total_cache - total_active_file - total_inactive_file << "\n";
    }

    free(file);
    return result;
}

static bool GetCGroupMemoryUsage_sscanf(const char* path, size_t* val, const char* const fields[], size_t fieldsLength)
{
    char line[1000];
    size_t fieldsFound = 0;
    size_t lineLength = sizeof(line) / sizeof(line[0]);
    FILE* file = fopen(path, "r");
    if (file == nullptr)
        return false;

    *val = 0;
    while (fgets(line, lineLength, file) != NULL)
    {
        for (size_t i = 0; i < fieldsLength; i++)
        {
            unsigned long value;
            if (sscanf(line, fields[i], &value))
            {
                fieldsFound++;
                *val += value;
            }
        }
    }

    fclose(file);

    if (fieldsFound == fieldsLength)
        return true;

    return false;
}

static bool GetCGroupMemoryUsage_stroul(const char* path, size_t* val, const char* const fields[], size_t fieldsLength, size_t lengths[])
{
    FILE* file = fopen(path, "r");
    if (file == nullptr)
        return false;

    char line[1000];
    size_t lineLength = sizeof(line) / sizeof(line[0]);
    size_t readValues = 0;
    char* endptr;

    *val = 0;
    while (fgets(line, lineLength, file) != NULL && readValues < fieldsLength)
    {
        for (size_t i = 0; i < fieldsLength; i++)
        {
            if (strncmp(line, fields[i], lengths[i]) == 0)
            {
                errno = 0;
                const char* startptr = line + lengths[i];
                *val += strtoll(startptr, &endptr, 10);
                if (endptr != startptr && errno == 0)
                    readValues++;

                break;
            }
        }
    }

    fclose(file);

    if (readValues == s_mem_stat_n_keys)
        return true;

    return false;
}

int main(int argc, char* argv[])
{
    MEMORYSTATUSEX memory_status_ex = {};
    memory_status_ex.dwLength = sizeof(memory_status_ex);
    auto b = GlobalMemoryStatusEx(&memory_status_ex);

    std::cout << "Hello World!\n";

    size_t nKeys = sizeof(CGROUP1_MEMSTAT_FIELDS) / sizeof(CGROUP1_MEMSTAT_FIELDS[0]);

    if (argc > 1)
    {
        std::cout << " GetCGroupMemoryUsage_sscanf" << std::endl;
        for (size_t j = 0; j < 5; j++)
        {
            size_t value;

            std::chrono::steady_clock::time_point begin = std::chrono::steady_clock::now();
            for (size_t i = 0; i < 10000; i++)
            {
                GetCGroupMemoryUsage_sscanf(argv[1], &value, CGROUP1_MEMSTAT_FIELDS, nKeys);
            }

            std::chrono::steady_clock::time_point end = std::chrono::steady_clock::now();
            std::cout << value << " Elapsed:" << std::chrono::duration_cast<std::chrono::microseconds>(end - begin).count() << std::endl;
        }

        size_t* lengths = new size_t[nKeys];
        for (size_t i = 0; i < nKeys; i++)
        {
            lengths[i] = strlen(CGROUP1_MEMSTAT_FIELDS_2[i]);
        }

        std::cout << " GetCGroupMemoryUsage_stroul" << std::endl;

        for (size_t j = 0; j < 5; j++)
        {
            size_t value;

            std::chrono::steady_clock::time_point begin = std::chrono::steady_clock::now();
            for (size_t i = 0; i < 10000; i++)
            {
                GetCGroupMemoryUsage_stroul(argv[1], &value, CGROUP1_MEMSTAT_FIELDS_2, nKeys, lengths);
            }

            std::chrono::steady_clock::time_point end = std::chrono::steady_clock::now();
            std::cout << value << " Elapsed:" << std::chrono::duration_cast<std::chrono::microseconds>(end - begin).count() << std::endl;
        }
    }
}


// Run program: Ctrl + F5 or Debug > Start Without Debugging menu
// Debug program: F5 or Debug > Start Debugging menu

// Tips for Getting Started: 
//   1. Use the Solution Explorer window to add/manage files
//   2. Use the Team Explorer window to connect to source control
//   3. Use the Output window to see build output and other messages
//   4. Use the Error List window to view errors
//   5. Go to Project > Add New Item to create new code files, or Project > Add Existing Item to add existing code files to the project
//   6. In the future, to open this project again, go to File > Open > Project and select the .sln file
