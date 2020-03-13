#!/bin/bash

dotnet restore dotnet-reverse.csproj
dotnet build dotnet-reverse.csproj -c Debug --no-restore
dotnet build dotnet-reverse.csproj -c Release --no-restore
